using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameReaderCommon;
using Newtonsoft.Json;
using SimHub.Plugins;

namespace RemotePitwall
{
    public class Settings
    {
        public string Url { get; set; } = "";
        public string Token { get; set; } = "";
    }

    [PluginDescription("Streams live session telemetry to a Remote Pitwall relay")]
    [PluginAuthor("stephanrutter")]
    [PluginName("Remote Pitwall")]
    public class RemotePitwallPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        const int SendIntervalMs = 50;      // 20 Hz
        const int ReconnectDelayMs = 3000;
        const int SendTimeoutMs = 5000;
        const int LapSettleMs = 1500;       // some sims update LastLapTime a moment after CompletedLaps
        const string GD = "DataCorePlugin.GameData.";

        // JSON key -> SimHub property name. Check names in SimHub under "Available properties".
        static readonly (string Key, string Prop)[] TickFields =
        {
            ("spd", GD + "SpeedKmh"), ("gear", GD + "Gear"), ("rpm", GD + "Rpms"), ("rpmMax", GD + "MaxRpm"),
            ("thr", GD + "Throttle"), ("brk", GD + "Brake"), ("clu", GD + "Clutch"),
            ("pos", GD + "Position"), ("lap", GD + "CurrentLap"), ("lapsTotal", GD + "TotalLaps"),
            ("cur", GD + "CurrentLapTime"), ("last", GD + "LastLapTime"), ("best", GD + "BestLapTime"),
            ("delta", "PersistantTrackerPlugin.SessionBestLiveDeltaSeconds"),
            ("fuel", GD + "Fuel"), ("fuelMax", GD + "MaxFuel"), ("fuelLaps", "DataCorePlugin.Computed.Fuel_RemainingLaps"),
            ("pit", GD + "IsInPitLane"), ("air", GD + "AirTemperature"), ("road", GD + "RoadTemperature"),
            ("tFL", GD + "TyreTemperatureFrontLeft"), ("tFR", GD + "TyreTemperatureFrontRight"),
            ("tRL", GD + "TyreTemperatureRearLeft"), ("tRR", GD + "TyreTemperatureRearRight"),
            ("pFL", GD + "TyrePressureFrontLeft"), ("pFR", GD + "TyrePressureFrontRight"),
            ("pRL", GD + "TyrePressureRearLeft"), ("pRR", GD + "TyrePressureRearRight"),
            ("wFL", GD + "TyreWearFrontLeft"), ("wFR", GD + "TyreWearFrontRight"),
            ("wRL", GD + "TyreWearRearLeft"), ("wRR", GD + "TyreWearRearRight"),
            ("pUnit", GD + "TyrePressureUnit"),
            ("flagG", GD + "Flag_Green"), ("flagY", GD + "Flag_Yellow"), ("flagB", GD + "Flag_Blue"),
            ("flagW", GD + "Flag_White"), ("flagK", GD + "Flag_Black"), ("flagC", GD + "Flag_Checkered"),
        };

        static readonly Stopwatch Clock = Stopwatch.StartNew();

        public Settings Settings;
        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "Remote Pitwall";

        // Outgoing messages: the sender loop always takes the newest one, older ones are overwritten.
        string pendingTick, pendingState;
        volatile string lastState;          // re-sent after every reconnect
        readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        volatile ClientWebSocket socket;
        volatile string status = "Idle";
        long nextTickAt;

        // Session and lap tracking (only touched from DataUpdate)
        string sessionKey;
        Dictionary<string, object> session = new Dictionary<string, object>();
        readonly List<Dictionary<string, object>> laps = new List<Dictionary<string, object>>();
        int lastCompleted = -1, pendingLap, stateVersion, publishedStateVersion = -1;
        long pendingLapAt;
        double? lapStartFuel, pendingFuel;

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("GeneralSettings", () => new Settings());
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Task.Run(() => SendLoop(stop.Token));
        }

        public void End(PluginManager pluginManager)
        {
            stop.Cancel();
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public void DataUpdate(PluginManager pm, ref GameData data)
        {
            if (!data.GameRunning) return;

            long now = Clock.ElapsedMilliseconds;
            TrackLaps(pm, now);

            if (stateVersion != publishedStateVersion)
            {
                publishedStateVersion = stateVersion;
                lastState = JsonConvert.SerializeObject(new Dictionary<string, object>(session) { ["laps"] = laps });
                Queue(ref pendingState, lastState);
            }

            if (now < nextTickAt) return;
            nextTickAt = now + SendIntervalMs;
            var tick = new Dictionary<string, object>(TickFields.Length);
            foreach (var (key, prop) in TickFields) tick[key] = Clean(pm.GetPropertyValue(prop));
            Queue(ref pendingTick, JsonConvert.SerializeObject(tick));
        }

        void TrackLaps(PluginManager pm, long now)
        {
            var info = new Dictionary<string, object>
            {
                ["game"] = pm.GetPropertyValue("DataCorePlugin.CurrentGame"),
                ["track"] = pm.GetPropertyValue(GD + "TrackName"),
                ["car"] = pm.GetPropertyValue(GD + "CarModel"),
                ["session"] = pm.GetPropertyValue(GD + "SessionTypeName"),
            };
            string key = string.Join("|", info.Values);
            int completed = Convert.ToInt32(pm.GetPropertyValue(GD + "CompletedLaps") ?? 0, CultureInfo.InvariantCulture);
            double? fuel = ToDouble(pm.GetPropertyValue(GD + "Fuel"));

            if (key != sessionKey || completed < lastCompleted)
            {
                sessionKey = key;
                session = info;
                laps.Clear();
                pendingLap = 0;
                lapStartFuel = null; // first lap after joining is partial, so its fuel use is unknown
                stateVersion++;
            }
            else if (completed > lastCompleted)
            {
                pendingLap = completed;
                pendingLapAt = now + LapSettleMs;
                pendingFuel = lapStartFuel - fuel;
                lapStartFuel = fuel;
            }
            lastCompleted = completed;

            if (pendingLap > 0 && now >= pendingLapAt)
            {
                laps.Add(new Dictionary<string, object>
                {
                    ["n"] = pendingLap,
                    ["t"] = Clean(pm.GetPropertyValue(GD + "LastLapTime")),
                    ["s1"] = Clean(pm.GetPropertyValue(GD + "Sector1LastLapTime")),
                    ["s2"] = Clean(pm.GetPropertyValue(GD + "Sector2LastLapTime")),
                    ["s3"] = Clean(pm.GetPropertyValue(GD + "Sector3LastLapTime")),
                    ["fuel"] = pendingFuel > 0 ? Math.Round(pendingFuel.Value, 2) : (double?)null, // negative = refuelled
                });
                pendingLap = 0;
                stateVersion++;
            }
        }

        void Queue(ref string slot, string json)
        {
            Volatile.Write(ref slot, json);
            Wake();
        }

        void Wake()
        {
            if (wake.CurrentCount == 0)
            {
                try { wake.Release(); } catch (SemaphoreFullException) { } // already signalled
            }
        }

        async Task SendLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(Settings?.Url)) { status = "Set the server URL"; await Delay(1000, ct); continue; }
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        socket = ws;
                        ws.Options.SetRequestHeader("Authorization", "Bearer " + Settings.Token);
                        status = "Connecting";
                        await ws.ConnectAsync(PubUri(Settings.Url), ct).ConfigureAwait(false);
                        status = "Connected, waiting for a game";
                        Volatile.Write(ref pendingState, lastState);

                        while (ws.State == WebSocketState.Open)
                        {
                            var state = Interlocked.Exchange(ref pendingState, null);
                            if (state != null) await Send(ws, "state " + state, ct).ConfigureAwait(false);
                            var tick = Interlocked.Exchange(ref pendingTick, null);
                            if (tick != null) { await Send(ws, "tick " + tick, ct).ConfigureAwait(false); status = "Streaming"; }
                            if (state == null && tick == null) await wake.WaitAsync(ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) { status = "Error: " + e.GetBaseException().Message; }
                finally { socket = null; }
                await Delay(ReconnectDelayMs, ct);
            }
        }

        static async Task Send(ClientWebSocket ws, string msg, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(SendTimeoutMs); // a stalled connection gets aborted and reconnected
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, timeout.Token)
                    .ConfigureAwait(false);
            }
        }

        static async Task Delay(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        // https://host -> wss://host/pub, http://host -> ws://host/pub
        static Uri PubUri(string url)
        {
            var b = new UriBuilder(url.TrimEnd('/') + "/pub");
            b.Scheme = b.Scheme == "https" ? "wss" : "ws";
            b.Port = b.Uri.IsDefaultPort ? -1 : b.Port;
            return b.Uri;
        }

        // TimeSpans -> seconds, doubles rounded to keep the payload small.
        static object Clean(object v)
        {
            switch (v)
            {
                case TimeSpan ts: return ts.Ticks <= 0 ? (object)null : Math.Round(ts.TotalSeconds, 3);
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? (object)null : Math.Round(d, 3);
                case float f: return Clean((double)f);
                default: return v;
            }
        }

        static double? ToDouble(object v) => v == null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

        void SettingsChanged()
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
            socket?.Abort(); // reconnect with the new URL/token
            Wake();
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            var url = new TextBox { Text = Settings.Url };
            var token = new PasswordBox { Password = Settings.Token };
            var statusText = new TextBlock { Margin = new Thickness(0, 14, 0, 0) };
            url.LostFocus += (s, e) => { if (Settings.Url != url.Text.Trim()) { Settings.Url = url.Text.Trim(); SettingsChanged(); } };
            token.LostFocus += (s, e) => { if (Settings.Token != token.Password.Trim()) { Settings.Token = token.Password.Trim(); SettingsChanged(); } };

            var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new TextBlock { Text = "Server URL, e.g. https://pitwall.example.com" });
            panel.Children.Add(url);
            panel.Children.Add(new TextBlock { Text = "Token (TOKEN from the server's .env)", Margin = new Thickness(0, 10, 0, 0) });
            panel.Children.Add(token);
            panel.Children.Add(statusText);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => statusText.Text = "Status: " + status;
            var control = new UserControl { Content = panel };
            control.Loaded += (s, e) => timer.Start();
            control.Unloaded += (s, e) => timer.Stop();
            return control;
        }
    }
}
