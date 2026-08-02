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
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unicode"
)

// ErrInvalidSlug is returned by NewsSlugPath when a slug cannot be turned into
// a path inside the news directory — either it failed IsSafeNewsSlug or the
// joined path escaped base. It is one error for both cases on purpose: the
// caller answers 400 either way, and telling a client WHICH check it tripped is
// free help for anyone probing the traversal guard.
var ErrInvalidSlug = errors.New("invalid slug")

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
//
// v is an any, so marshalling can genuinely fail (a channel, a func, a cycle).
// The old code ignored that and wrote the nil buffer, which answered 200 with an
// empty body — the admin UI then reported "unexpected end of JSON input" with
// nothing in the journal to explain it. Nothing has been written to the socket
// yet at this point, so a real 500 is still possible.
func WriteJSON(w http.ResponseWriter, v any) {
	b, err := json.Marshal(v)
	if err != nil {
		Fail(w, http.StatusInternalServerError, "failed to encode the response", "adminutil", err)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	// A write failure here is a client that hung up mid-response; there is no
	// status left to change and nothing useful to log per request.
	_, _ = w.Write(b)
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
	// 0o750, not 0o755: nginx serves this tree as www-data, the same user both
	// services run as (deploy/systemd/*.service), so the group bit is all it
	// needs. The units already set UMask=0027, which strips the world bits
	// anyway — this only makes the code agree with the deployment.
	if err := os.MkdirAll(dir, 0o750); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, "."+filepath.Base(path)+".tmp-*")
	if err != nil {
		return err
	}
	tmpPath := tmp.Name()
	cleanup := func() {
		_ = tmp.Close()
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

// isASCIIAlnum reports whether r is [A-Za-z0-9].
//
// The guards below all start from this class and only differ in which
// punctuation they add. Spelling the three ranges out at every call site is how
// a check ends up subtly different from its neighbours — and one loose guard
// here is one path that reaches filepath.Join.
func isASCIIAlnum(r rune) bool {
	return (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9')
}

// isASCIIHexDigit reports whether r is [0-9A-Fa-f].
func isASCIIHexDigit(r rune) bool {
	return (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F')
}

// IsSafeGameID allows only [A-Za-z0-9_-] for game IDs and not empty.
func IsSafeGameID(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	for _, r := range s {
		if isASCIIAlnum(r) || r == '-' || r == '_' {
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
		if isASCIIAlnum(r) || r == '-' || r == '_' || r == '.' {
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
		if isASCIIHexDigit(r) {
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
		return "", ErrInvalidSlug
	}
	p := filepath.Join(base, slug+".md")
	if !EnsureWithin(base, p) {
		return "", ErrInvalidSlug
	}
	return p, nil
}

// SanitizeFilename keeps only safe characters for filenames.
//
// The result is stored in the asset tree that nginx serves under /assets/, so it
// must stay URL-safe ASCII. Replacing every non-ASCII rune with '_' did that but
// mapped every name of the same length onto the same file: "скриншот.png" and
// "картинка.png" both became "________.png", and the second upload silently
// overwrote the first — a published post then showed the wrong picture with no
// error anywhere.
//
// Cyrillic is therefore transliterated so the name stays readable, and any name
// that contained non-ASCII at all also gets a short hash of the original
// appended. The hash is what actually guarantees distinctness: transliteration
// is not injective ("е" and "э" both become "e"), and scripts with no table
// entry still collapse to '_'. It is derived from the input, not random, so
// re-uploading the same file still replaces its own asset instead of piling up
// copies.
//
// Pure ASCII input is returned exactly as before, so names of already stored
// assets — including the "________.png" ones — are unaffected.
func SanitizeFilename(name string) string {
	safe, hadNonASCII := sanitizeToASCII(name)
	if !hadNonASCII {
		if safe == "" {
			return "file"
		}
		return safe
	}
	stem, ext := splitFileExt(safe)
	// A stem that starts with '.' or '-' is a hidden file or a flag-looking
	// argument; both are reachable here because the leading rune was dropped
	// ("ьскриншот.png").
	stem = strings.TrimLeft(stem, ".-")
	// Nothing survived transliteration ("文件.png", "ъь.png"): the hash alone
	// would be a usable but anonymous name, so give it the stem an empty name
	// gets.
	if strings.Trim(stem, "_") == "" {
		stem = "file"
	}
	return stem + "-" + shortNameHash(name) + ext
}

// sanitizeToASCII maps name onto the safe character set and reports whether it
// held any non-ASCII rune (which is what makes the mapping lossy).
func sanitizeToASCII(name string) (string, bool) {
	var b strings.Builder
	b.Grow(len(name))
	hadNonASCII := false
	for _, r := range name {
		if r < 0x80 {
			if isASCIIAlnum(r) || r == '.' || r == '-' || r == '_' {
				b.WriteRune(r)
			} else {
				b.WriteByte('_')
			}
			continue
		}
		hadNonASCII = true
		if s, ok := translitRune(r); ok {
			b.WriteString(s) // may be empty: "ъ" and "ь" have no Latin equivalent
			continue
		}
		b.WriteByte('_')
	}
	return b.String(), hadNonASCII
}

// cyrillicTranslit maps lowercase Cyrillic letters onto ASCII. Uppercase is
// derived from it by translitRune, so only one row per letter is maintained.
var cyrillicTranslit = map[rune]string{
	'а': "a", 'б': "b", 'в': "v", 'г': "g", 'д': "d", 'е': "e", 'ё': "yo",
	'ж': "zh", 'з': "z", 'и': "i", 'й': "j", 'к': "k", 'л': "l", 'м': "m",
	'н': "n", 'о': "o", 'п': "p", 'р': "r", 'с': "s", 'т': "t", 'у': "u",
	'ф': "f", 'х': "h", 'ц': "c", 'ч': "ch", 'ш': "sh", 'щ': "sch",
	'ъ': "", 'ы': "y", 'ь': "", 'э': "e", 'ю': "yu", 'я': "ya",
	// Ukrainian and Belarusian letters that are not in the Russian alphabet.
	'і': "i", 'ї': "yi", 'є': "ye", 'ґ': "g", 'ў': "u",
}

// translitRune returns the ASCII spelling of a Cyrillic rune, keeping the case
// of the first letter so that "Скриншот" stays "Skrinshot".
func translitRune(r rune) (string, bool) {
	if s, ok := cyrillicTranslit[r]; ok {
		return s, true
	}
	lower := unicode.ToLower(r)
	if lower == r {
		return "", false
	}
	s, ok := cyrillicTranslit[lower]
	if !ok || s == "" {
		return s, ok
	}
	return strings.ToUpper(s[:1]) + s[1:], true
}

// splitFileExt splits an already sanitised name into its stem and a trailing
// ".ext", so a disambiguating suffix lands before the extension rather than
// after it — "file.png-a1b2c3d4" would be served as a nameless type.
func splitFileExt(name string) (string, string) {
	i := strings.LastIndexByte(name, '.')
	if i < 0 || i == len(name)-1 {
		return name, ""
	}
	ext := name[i+1:]
	if len(ext) > 8 {
		return name, ""
	}
	for _, r := range ext {
		if !isASCIIAlnum(r) {
			return name, ""
		}
	}
	return name[:i], name[i:]
}

// shortNameHash returns 8 hex characters derived from the original name.
func shortNameHash(name string) string {
	sum := sha256.Sum256([]byte(name))
	return hex.EncodeToString(sum[:4])
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
//
// The interface IS the return value here: callers must not care whether they
// got the real http.Flusher or NoopFlusher, which is the whole point.
//
//nolint:ireturn // See above: the fallback only works through an interface.
func FlusherFor(w http.ResponseWriter) Flusher {
	if f, ok := w.(http.Flusher); ok {
		return f
	}
	return NoopFlusher{}
}

// contentRootSearchDepth is how many directories above the executable are
// searched for a "content" directory.
const contentRootSearchDepth = 6

// DetectContentRoot resolves the content root from CONTENT_ROOT, then by
// walking up from the executable, then from the working directory.
func DetectContentRoot() string {
	if v := os.Getenv("CONTENT_ROOT"); v != "" {
		return v
	}
	if exe, err := os.Executable(); err == nil && exe != "" {
		d := filepath.Dir(exe)
		for range contentRootSearchDepth {
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
