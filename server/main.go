// Remote Pitwall relay: the SimHub plugin connects to /pub over WebSocket and sends
// text messages "<event> <json>" (event is tick or state); browsers receive them
// via Server-Sent Events on /events.
package main

import (
	"bytes"
	"crypto/subtle"
	_ "embed"
	"io"
	"log"
	"net/http"
	"os"
	"sync"
	"time"

	"github.com/coder/websocket"
)

//go:embed index.html
var indexHTML []byte

// Replayed to new viewers in this order so the page has session info before the first tick.
var eventNames = []string{"state", "tick"}

type frame struct {
	data []byte
	at   time.Time
}

type hub struct {
	mu   sync.Mutex
	last map[string]frame
	subs map[chan []byte]struct{}
}

func (h *hub) publish(name string, body []byte) {
	// SSE data must be a single line; JSON whitespace newlines can safely become spaces.
	body = bytes.ReplaceAll(bytes.ReplaceAll(body, []byte("\r"), nil), []byte("\n"), []byte(" "))
	msg := append([]byte("event: "+name+"\ndata: "), body...)
	msg = append(msg, "\n\n"...)

	h.mu.Lock()
	defer h.mu.Unlock()
	h.last[name] = frame{msg, time.Now()}
	for c := range h.subs {
		select {
		case c <- msg:
		default: // viewer too slow, drop the frame
		}
	}
}

func (h *hub) events(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "streaming unsupported", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")

	c := make(chan []byte, 32)
	var initial [][]byte
	h.mu.Lock()
	for _, n := range eventNames {
		f, ok := h.last[n]
		if !ok || (n == "tick" && time.Since(f.at) > 5*time.Second) {
			continue // a stale tick would make the page briefly show LIVE
		}
		initial = append(initial, f.data)
	}
	h.subs[c] = struct{}{}
	h.mu.Unlock()
	defer func() {
		h.mu.Lock()
		delete(h.subs, c)
		h.mu.Unlock()
	}()

	for _, m := range initial {
		w.Write(m)
	}
	flusher.Flush()

	ping := time.NewTicker(20 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case m := <-c:
			w.Write(m)
			flusher.Flush()
		case <-ping.C:
			io.WriteString(w, ": ping\n\n")
			flusher.Flush()
		}
	}
}

func main() {
	token := os.Getenv("TOKEN")
	if token == "" {
		log.Fatal("TOKEN env var is required")
	}
	auth := []byte("Bearer " + token)
	h := &hub{last: map[string]frame{}, subs: map[chan []byte]struct{}{}}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Write(indexHTML)
	})
	mux.HandleFunc("GET /events", h.events)
	mux.HandleFunc("GET /pub", func(w http.ResponseWriter, r *http.Request) {
		if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), auth) != 1 {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		c, err := websocket.Accept(w, r, nil)
		if err != nil {
			return
		}
		defer c.CloseNow()
		c.SetReadLimit(256 << 10)
		log.Println("publisher connected from", r.RemoteAddr)
		for {
			_, msg, err := c.Read(r.Context())
			if err != nil {
				log.Println("publisher disconnected:", err)
				return
			}
			name, body, _ := bytes.Cut(msg, []byte(" "))
			if n := string(name); n == "tick" || n == "state" {
				h.publish(n, body)
			}
		}
	})

	srv := &http.Server{Addr: ":8080", Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	log.Println("listening on :8080")
	log.Fatal(srv.ListenAndServe())
}
