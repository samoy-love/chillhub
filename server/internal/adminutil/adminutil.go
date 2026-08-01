// Package adminutil holds primitives shared by every admin API domain:
// request-method checks, JSON responses, ID generation and — most importantly —
// the path-safety guards.
//
// The guards (IsSafeGameID, IsSafeVersion, IsSafeNewsSlug, NewsSlugPath,
// EnsureWithin, SanitizeFilename, SanitizeAssetPath) live here and only here on
// purpose: a second copy of a traversal check is a second chance to get it
// wrong. Domain packages must import them, never reimplement them.
package adminutil

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unicode"
)

// RequireMethod reports whether the request may proceed. OPTIONS is let through
// (the CORS middleware answers preflight); any other mismatch is answered with
// 405 and the handler must return.
func RequireMethod(w http.ResponseWriter, r *http.Request, method string) bool {
	if r.Method == http.MethodOptions {
		return true
	}
	if r.Method != method {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return false
	}
	return true
}

// Fail answers with a generic message and puts the real error in the log.
//
// A filesystem error stringifies to something like
// "open /srv/chillhub/content/news/x.md: permission denied" — the absolute
// content root, the layout of the deployment and often the reason. That belongs
// in the journal, not in an HTTP body: several of these endpoints are reachable
// without authentication (nginx bypasses auth_request for the upload routes,
// and /feedback/submit is public by design), and the admin panel has nothing to
// do with the path anyway.
func Fail(w http.ResponseWriter, code int, publicMsg, tag string, err error) {
	if err != nil {
		log.Printf("[%s] %v", tag, err)
	}
	http.Error(w, publicMsg, code)
}

// WriteJSON writes v as application/json with caching disabled.
func WriteJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	b, _ := json.Marshal(v)
	w.Write(b)
}

// WriteFileAtomic writes data to path through a temporary file in the same
// directory followed by a rename.
//
// Every state file this server keeps — manifests, latest.json, the games
// registry, news index.json and news_meta.json — is read by the PUBLIC API
// while the admin API rewrites it. A plain os.WriteFile truncates first, so a
// crash, a full disk or simply an unlucky read in between hands out a truncated
// JSON document; a rename is atomic and readers see either the old file or the
// new one.
func WriteFileAtomic(path string, data []byte, perm os.FileMode) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, "."+filepath.Base(path)+".tmp-*")
	if err != nil {
		return err
	}
	tmpPath := tmp.Name()
	cleanup := func() {
		tmp.Close()
		_ = os.Remove(tmpPath)
	}
	if _, err := tmp.Write(data); err != nil {
		cleanup()
		return err
	}
	if err := tmp.Sync(); err != nil {
		cleanup()
		return err
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(tmpPath)
		return err
	}
	if err := os.Chmod(tmpPath, perm); err != nil {
		_ = os.Remove(tmpPath)
		return err
	}
	if err := os.Rename(tmpPath, path); err != nil {
		_ = os.Remove(tmpPath)
		return err
	}
	return nil
}

// EnsureWithin reports whether p resolves to a location inside base.
func EnsureWithin(base, p string) bool {
	b, _ := filepath.Abs(base)
	q, _ := filepath.Abs(p)
	rel, err := filepath.Rel(b, q)
	if err != nil {
		return false
	}
	if rel == "" || rel == ".." {
		return false
	}
	// Only a ".." SEGMENT means "outside base". A plain HasPrefix(rel, "..")
	// also rejected legitimate names that merely begin with two dots — "..foo",
	// "..gitkeep" — which are ordinary files inside base.
	return !strings.HasPrefix(rel, ".."+string(filepath.Separator))
}

// IsSafeGameID allows only [A-Za-z0-9_-] for game IDs and not empty.
func IsSafeGameID(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' {
			continue
		}
		return false
	}
	return true
}

// IsSafeVersion allows [0-9A-Za-z._-] for version labels (e.g., semver with
// pre-release), not empty.
func IsSafeVersion(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' || r == '.' {
			continue
		}
		return false
	}
	return true
}

// IsHexID reports whether s is a plain lower/upper-case hex identifier of a
// plausible length. Both GenID and NewBuildID emit exactly that, so every
// server-generated id (upload ids, build ids) that comes back from a client and
// is turned into a path must pass this check before it reaches filepath.Join.
func IsHexID(s string) bool {
	if len(s) < 8 || len(s) > 64 {
		return false
	}
	for _, r := range s {
		if (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F') {
			continue
		}
		return false
	}
	return true
}

// IsSafeNewsSlug reports whether s can be used as a news file name.
// Slugs are produced by the admin UI from the article title and may contain
// Cyrillic, so unicode letters and digits are allowed alongside [-._]; anything
// that could escape the news directory (separators, "..", dot-files) is not.
func IsSafeNewsSlug(s string) bool {
	if s == "" || len(s) > 128 {
		return false
	}
	if strings.Contains(s, "..") || strings.HasPrefix(s, ".") || strings.HasPrefix(s, "-") {
		return false
	}
	for _, r := range s {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			continue
		}
		if r == '-' || r == '_' || r == '.' {
			continue
		}
		return false
	}
	return true
}

// NewsSlugPath resolves the markdown file of a slug inside base.
// The slug is attacker-controlled, so it is validated and the joined path is
// verified to stay inside base.
func NewsSlugPath(base, slug string) (string, error) {
	if !IsSafeNewsSlug(slug) {
		return "", fmt.Errorf("invalid slug")
	}
	p := filepath.Join(base, slug+".md")
	if !EnsureWithin(base, p) {
		return "", fmt.Errorf("invalid slug")
	}
	return p, nil
}

// SanitizeFilename keeps only safe characters for filenames.
func SanitizeFilename(name string) string {
	safe := make([]rune, 0, len(name))
	for _, r := range name {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '.' || r == '-' || r == '_' {
			safe = append(safe, r)
		} else {
			safe = append(safe, '_')
		}
	}
	if len(safe) == 0 {
		return "file"
	}
	return string(safe)
}

// SanitizeAssetPath normalizes a relative directory path inside the asset tree.
// The result must still be checked with EnsureWithin after joining.
func SanitizeAssetPath(p string) string {
	p = filepath.ToSlash(strings.TrimSpace(p))
	p = strings.TrimPrefix(p, "/")
	p = strings.Trim(p, "/")
	p = strings.ReplaceAll(p, "..", "_")
	return p
}

// GenID returns a random 12-byte hex identifier.
func GenID() string {
	var b [12]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("id-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

// NewBuildID returns a random 16-byte hex identifier.
func NewBuildID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("build-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

// Flusher is the minimal interface the NDJSON streaming handlers need.
type Flusher interface{ Flush() }

// NoopFlusher is used as a fallback when the writer doesn't implement
// http.Flusher (httptest recorders, for instance).
type NoopFlusher struct{}

// Flush does nothing.
func (NoopFlusher) Flush() {}

// FlusherFor returns the ResponseWriter's Flusher, or a no-op one.
func FlusherFor(w http.ResponseWriter) Flusher {
	if f, ok := w.(http.Flusher); ok {
		return f
	}
	return NoopFlusher{}
}

// DetectContentRoot resolves the content root from CONTENT_ROOT, then by
// walking up from the executable, then from the working directory.
func DetectContentRoot() string {
	if v := os.Getenv("CONTENT_ROOT"); v != "" {
		return v
	}
	if exe, err := os.Executable(); err == nil && exe != "" {
		d := filepath.Dir(exe)
		for i := 0; i < 6; i++ {
			p := filepath.Join(d, "content")
			if st, err := os.Stat(p); err == nil && st.IsDir() {
				return p
			}
			d = filepath.Dir(d)
		}
	}
	if st, err := os.Stat("content"); err == nil && st.IsDir() {
		return "content"
	}
	return "."
}
