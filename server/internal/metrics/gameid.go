package metrics

import (
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminutil"
)

// The per-game breakdown of the admin panel filled up with several hundred rows
// named like 00d378defbff4348ab226f84361fec64 — all counters zero. Nothing in
// the panel could tell them from a real game, because /metrics/report accepted
// any gameId at all: it was clamped to maxGameID and stored.
//
// That is worse than a cosmetic problem. The endpoint is public and
// unauthenticated, gameId is a Prometheus label on half the counters in prom.go,
// and one label value per submission is unbounded cardinality handed to a
// stranger.
//
// So a gameId now has to name a game that actually exists. The registry is the
// same file the public API serves its catalogue from, and it is read through a
// cache keyed on the file's mtime and size: a submission must not cost a disk
// read, and a game added in the admin panel must not need a restart.

// registryFileMaxBytes caps the registry read. The file is a few hundred bytes
// per game; anything past this is not a registry we should be parsing.
const registryFileMaxBytes = 4 << 20

// gameRegistry caches the set of known game IDs.
//
// A miss is never fatal: if the file cannot be read or parsed, known() reports
// hasRegistry=false and the caller falls back to the format check alone.
// Dropping every event because one file is briefly unreadable would turn a
// deploy hiccup into a hole in the statistics.
type gameRegistry struct {
	path string

	mu      sync.Mutex
	ids     map[string]bool
	modTime time.Time
	size    int64
	loaded  bool
}

func newGameRegistry(root string) *gameRegistry {
	return &gameRegistry{path: filepath.Join(root, "manifests", "_registry", "games.json")}
}

// known returns the cached ID set, reloading it when the file changed.
// hasRegistry is false when there is no usable registry to check against.
func (g *gameRegistry) known() (ids map[string]bool, hasRegistry bool) {
	st, err := os.Stat(g.path)
	if err != nil {
		return nil, false
	}

	g.mu.Lock()
	defer g.mu.Unlock()
	if g.loaded && g.modTime.Equal(st.ModTime()) && g.size == st.Size() {
		return g.ids, true
	}
	ids, err = readRegistryIDs(g.path)
	if err != nil {
		return nil, false
	}
	g.ids, g.modTime, g.size, g.loaded = ids, st.ModTime(), st.Size(), true
	return g.ids, true
}

func readRegistryIDs(path string) (map[string]bool, error) {
	// #nosec G304 -- path is the configured content root plus three constant
	// components. No part of it comes from a request.
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer func() { _ = f.Close() }()

	var reg struct {
		Items []struct {
			GameID string `json:"gameId"`
		} `json:"items"`
	}
	dec := json.NewDecoder(io.LimitReader(f, registryFileMaxBytes))
	if err := dec.Decode(&reg); err != nil {
		return nil, err
	}
	ids := make(map[string]bool, len(reg.Items))
	for _, it := range reg.Items {
		if id := strings.TrimSpace(it.GameID); id != "" {
			ids[id] = true
		}
	}
	return ids, nil
}

// gameIDOK reports whether an event may be stored with this gameId.
//
// An empty gameId is fine: launcher_start names no game.
func (h *Handlers) gameIDOK(gid string) bool {
	if gid == "" {
		return true
	}
	// The format gate runs first and always. A leading underscore is reserved
	// for internal directories such as _registry, exactly as publicGameID in
	// cmd/api treats it.
	if len(gid) > maxGameID || strings.HasPrefix(gid, "_") || !adminutil.IsSafeGameID(gid) {
		return false
	}
	ids, hasRegistry := h.games.known()
	if !hasRegistry {
		return true
	}
	return ids[gid]
}
