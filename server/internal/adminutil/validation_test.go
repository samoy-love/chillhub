package adminutil

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
)

// Every value checked here is turned into a filesystem path by some handler.
// The checks are the only thing between a client-supplied string and
// filepath.Join, so each one is pinned against the traversal forms that reach
// these endpoints in practice.

func TestIsSafeGameIDRejectsAnythingPathLike(t *testing.T) {
	bad := []string{
		"", "   ", "..", "../evil", "a/b", `a\b`, "/abs", "game id", "game.id",
		"игра", "a:b", "a*b", "a\x00b",
	}
	for _, s := range bad {
		if IsSafeGameID(s) {
			t.Errorf("IsSafeGameID(%q) = true", s)
		}
	}
	for _, s := range []string{"lethal-company", "raft", "my_game-1", "A1", "0"} {
		if !IsSafeGameID(s) {
			t.Errorf("IsSafeGameID(%q) = false, it is a real game id", s)
		}
	}
}

// A dot is rejected in a game id on purpose: ids become directory names under
// manifests/, and allowing dots reopens ".." one character at a time.
func TestIsSafeGameIDRejectsDots(t *testing.T) {
	for _, s := range []string{".", "..", "a.b", ".hidden", "game."} {
		if IsSafeGameID(s) {
			t.Errorf("IsSafeGameID(%q) = true", s)
		}
	}
}

// Versions DO allow dots — they are semver — so the traversal forms matter more here.
func TestIsSafeVersionAllowsSemverButNotTraversal(t *testing.T) {
	for _, s := range []string{"1.0.0", "1.2.3-rc.1", "2026_01", "v1.0.0", "1"} {
		if !IsSafeVersion(s) {
			t.Errorf("IsSafeVersion(%q) = false", s)
		}
	}
	for _, s := range []string{"", "   ", "1.0/../2", `1.0\..`, "1.0 0", "версия", "1.0;rm"} {
		if IsSafeVersion(s) {
			t.Errorf("IsSafeVersion(%q) = true", s)
		}
	}
}

// Ids that come back from a client and become paths must be recognisably
// server-generated.
func TestIsHexIDMatchesWhatTheServerGenerates(t *testing.T) {
	for range 20 {
		if id := GenID(); !IsHexID(id) {
			t.Fatalf("GenID produced %q, which IsHexID rejects", id)
		}
		if id := NewBuildID(); !IsHexID(id) {
			t.Fatalf("NewBuildID produced %q, which IsHexID rejects", id)
		}
	}
}

// Generated ids must actually differ: a build id collision would make one upload
// overwrite another's staging directory.
func TestGeneratedIDsAreUnique(t *testing.T) {
	seen := map[string]bool{}
	for i := range 500 {
		id := NewBuildID()
		if seen[id] {
			t.Fatalf("NewBuildID repeated %q after %d draws", id, i)
		}
		seen[id] = true
	}
}

func TestIsHexIDRejectsNonHexAndOutOfRangeLengths(t *testing.T) {
	for _, s := range []string{"", "abc", "1234567", strings.Repeat("a", 65), "../../etc", "zzzzzzzz", "12345678 "} {
		if IsHexID(s) {
			t.Errorf("IsHexID(%q) = true", s)
		}
	}
}

// The sanitiser is the last step before a name lands on disk: whatever it
// returns must be a single flat segment.
func TestSanitizeFilenameAlwaysReturnsOneFlatSegment(t *testing.T) {
	for _, in := range []string{
		"../../etc/passwd", `..\..\windows\system32`, "a/b/c", "a:b", "имя файла",
		"", "   ", "\x00\x01", "-rf", "file name (1).png",
	} {
		got := SanitizeFilename(in)
		if got == "" {
			t.Errorf("SanitizeFilename(%q) returned an empty name", in)
		}
		if strings.ContainsAny(got, `/\:`) {
			t.Errorf("SanitizeFilename(%q) = %q, which is still a path", in, got)
		}
	}
}

// Ordinary names must survive unchanged, or every asset URL churns on re-upload.
func TestSanitizeFilenameLeavesPlainNamesAlone(t *testing.T) {
	for _, s := range []string{"screenshot.png", "cover-1.jpg", "my_file.2.webp"} {
		if got := SanitizeFilename(s); got != s {
			t.Errorf("SanitizeFilename(%q) = %q", s, got)
		}
	}
}

// Two different Cyrillic names of the same length used to sanitise to the same
// row of underscores, so the second upload silently overwrote the first and a
// published post showed the wrong picture.
func TestSanitizeFilenameKeepsDifferentCyrillicNamesApart(t *testing.T) {
	names := []string{
		"скриншот.png", "картинка.png", "снимок01.png", "снимок02.png",
		"Скриншот.png", "экран.png", "екран.png", "мой файл.png",
		"文件.png", "画像.png", "🙂.png", "ﬁle.png", //nolint:gosmopolitan // Non-Latin test input is the point: these names must not collide.
	}
	seen := map[string]string{}
	for _, in := range names {
		got := SanitizeFilename(in)
		if prev, dup := seen[got]; dup {
			t.Errorf("SanitizeFilename(%q) = %q, which already belongs to %q", in, got, prev)
		}
		seen[got] = in
	}
}

// The stored name is served by nginx under /assets/, so it has to stay ASCII and
// URL-safe whatever the source alphabet was — and it must keep the extension
// last, or the browser gets a file with no type.
func TestSanitizeFilenameStaysURLSafeAndKeepsTheExtension(t *testing.T) {
	for _, in := range []string{"скриншот.png", "文件.png", "🙂🙂.jpeg", "ьъ.png", "имя файла.webp"} { //nolint:gosmopolitan // Non-Latin test input is the point.
		got := SanitizeFilename(in)
		for _, r := range got {
			if isASCIIAlnum(r) || r == '.' || r == '-' || r == '_' {
				continue
			}
			t.Fatalf("SanitizeFilename(%q) = %q, which contains the unsafe rune %q", in, got, r)
		}
		ext := filepath.Ext(in)
		if !strings.HasSuffix(got, ext) {
			t.Errorf("SanitizeFilename(%q) = %q, want it to still end in %q", in, got, ext)
		}
		if strings.TrimSuffix(got, ext) == "" {
			t.Errorf("SanitizeFilename(%q) = %q, a file with no name", in, got)
		}
	}
}

// A name with nothing transliterable left must still be usable, not a bare
// extension or a string of underscores that collides with the next one.
func TestSanitizeFilenameGivesFullyNonASCIINamesAStem(t *testing.T) {
	for _, in := range []string{"文件", "🙂", "ъь", "日本語.png"} { //nolint:gosmopolitan // Non-Latin test input is the point.
		got := SanitizeFilename(in)
		stem := strings.TrimSuffix(got, filepath.Ext(got))
		if strings.Trim(stem, "_") == "" {
			t.Errorf("SanitizeFilename(%q) = %q, whose stem is empty or all underscores", in, got)
		}
	}
}

// Cyrillic is transliterated rather than blanked, so the URL stays readable
// instead of turning into a hash with no hint of what the picture is.
func TestSanitizeFilenameTransliteratesCyrillic(t *testing.T) {
	for in, want := range map[string]string{
		"скриншот.png": "skrinshot",
		"Картинка.jpg": "Kartinka",
		"обложка":      "oblozhka",
	} {
		if got := SanitizeFilename(in); !strings.HasPrefix(got, want+"-") {
			t.Errorf("SanitizeFilename(%q) = %q, want it to start with %q", in, got, want+"-")
		}
	}
}

// The same source name must keep producing the same stored name: re-uploading a
// corrected image is expected to replace the asset the post already links to.
func TestSanitizeFilenameIsStableAndIdempotent(t *testing.T) {
	for _, in := range []string{"скриншот.png", "文件.png", "screenshot.png"} { //nolint:gosmopolitan // Non-Latin test input is the point.
		first := SanitizeFilename(in)
		if second := SanitizeFilename(in); second != first {
			t.Errorf("SanitizeFilename(%q) is not stable: %q then %q", in, first, second)
		}
		// The upload path sanitises the name, splits the extension off and
		// sanitises the stem again before storing it.
		if again := SanitizeFilename(first); again != first {
			t.Errorf("SanitizeFilename(%q) = %q, re-sanitising it gives %q", in, first, again)
		}
	}
}

// ASCII names must come through byte for byte: every asset already stored is
// addressed by the name this function produced, and the URLs are in published
// posts.
func TestSanitizeFilenameLeavesASCIIBehaviourUnchanged(t *testing.T) {
	for in, want := range map[string]string{
		"screenshot.png":     "screenshot.png",
		"cover-1.jpg":        "cover-1.jpg",
		"my_file.2.webp":     "my_file.2.webp",
		"file name (1).png":  "file_name__1_.png",
		"________.jpg":       "________.jpg",
		"../../etc/passwd":   ".._.._etc_passwd", // #nosec G101 -- a traversal fixture, not a credential.
		"":                   "file",
		"   ":                "___",
		"UPPER.PNG":          "UPPER.PNG",
		"a-b_c.1.2.3.tar.gz": "a-b_c.1.2.3.tar.gz",
	} {
		if got := SanitizeFilename(in); got != want {
			t.Errorf("SanitizeFilename(%q) = %q, want %q", in, got, want)
		}
	}
}

// The asset subdirectory comes from a form field and is joined onto the assets
// root; "..", leading slashes and backslashes must not survive.
func TestSanitizeAssetPathStripsTraversal(t *testing.T) {
	base := t.TempDir()
	for _, in := range []string{"../../etc", "/etc", "//etc", "news/../../etc", `..\..\etc`, "..", "./.."} {
		rel := SanitizeAssetPath(in)
		if strings.Contains(rel, "..") {
			t.Errorf("SanitizeAssetPath(%q) = %q, still contains ..", in, rel)
		}
		if !EnsureWithin(base, filepath.Join(base, rel)) {
			t.Errorf("SanitizeAssetPath(%q) = %q escapes the base", in, rel)
		}
	}
}

// A normal subdirectory must pass through: the admin files assets under news/.
func TestSanitizeAssetPathKeepsNormalSubdirectories(t *testing.T) {
	for in, want := range map[string]string{
		"news":        "news",
		"news/2026":   "news/2026",
		"/news/2026/": "news/2026",
		"  news  ":    "news",
		`news\2026`:   "news/2026",
		"":            "",
	} {
		if got := SanitizeAssetPath(in); got != want {
			t.Errorf("SanitizeAssetPath(%q) = %q, want %q", in, got, want)
		}
	}
}

// Mutating endpoints must refuse GET: the admin API authenticates with cookies,
// so a GET mutation is triggerable from any page the admin has open.
func TestRequireMethodRefusesTheWrongVerb(t *testing.T) {
	w := httptest.NewRecorder()
	if RequireMethod(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/save", nil), http.MethodPost) {
		t.Fatal("a GET was allowed through a POST-only guard")
	}
	if w.Code != http.StatusMethodNotAllowed {
		t.Errorf("status = %d, want 405", w.Code)
	}
}

// OPTIONS is let through so the CORS middleware can answer the preflight; a 405
// here makes the browser cancel the real request.
func TestRequireMethodLetsPreflightThrough(t *testing.T) {
	w := httptest.NewRecorder()
	if !RequireMethod(w, httptest.NewRequestWithContext(t.Context(), http.MethodOptions, "/admin/api/save", nil), http.MethodPost) {
		t.Fatal("a preflight was refused")
	}
}

func TestRequireMethodAllowsTheMatchingVerb(t *testing.T) {
	w := httptest.NewRecorder()
	if !RequireMethod(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/save", nil), http.MethodPost) {
		t.Fatalf("a matching POST was refused: %d", w.Code)
	}
}

// The real error goes to the journal, never to the client: several of these
// endpoints are reachable without authentication, and a filesystem error
// stringifies to the absolute content root.
func TestFailKeepsThePathOutOfTheResponse(t *testing.T) {
	w := httptest.NewRecorder()
	Fail(w, http.StatusInternalServerError, "failed to store the registry", "test",
		&pathError{"open /srv/chillhub/content/manifests/x.json: permission denied"})

	if w.Code != http.StatusInternalServerError {
		t.Errorf("status = %d", w.Code)
	}
	if strings.Contains(w.Body.String(), "/srv/chillhub") {
		t.Fatalf("the deployment path leaked into the response: %q", w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "failed to store the registry") {
		t.Errorf("the public message is missing: %q", w.Body.String())
	}
}

type pathError struct{ msg string }

func (e *pathError) Error() string { return e.msg }

// Admin API answers must never be cached: the panel polls them and a cached
// build list shows an upload that already finished as still running.
func TestWriteJSONDisablesCaching(t *testing.T) {
	w := httptest.NewRecorder()
	WriteJSON(w, map[string]any{"status": "ok", "n": 1})

	if ct := w.Header().Get("Content-Type"); !strings.Contains(ct, "application/json") {
		t.Errorf("Content-Type = %q", ct)
	}
	if cc := w.Header().Get("Cache-Control"); !strings.Contains(cc, "no-store") {
		t.Errorf("Cache-Control = %q, want no-store", cc)
	}
	var got map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &got); err != nil {
		t.Fatalf("body is not JSON: %v", err)
	}
	if got["status"] != "ok" {
		t.Errorf("payload lost: %v", got)
	}
}

// A writer that cannot flush must not crash the NDJSON streaming handlers.
func TestFlusherForFallsBackToANoop(_ *testing.T) {
	FlusherFor(httptest.NewRecorder()).Flush() // recorders do implement Flusher
	FlusherFor(nonFlusher{httptest.NewRecorder()}).Flush()
}

type nonFlusher struct{ http.ResponseWriter }

// CONTENT_ROOT wins over any autodetection — that is how the systemd unit points
// the service at /srv/chillhub/content.
func TestDetectContentRootHonoursTheEnvironment(t *testing.T) {
	t.Setenv("CONTENT_ROOT", "/srv/chillhub/content")
	if got := DetectContentRoot(); got != "/srv/chillhub/content" {
		t.Fatalf("DetectContentRoot = %q, want the configured root", got)
	}
}

// Without the variable the result must still be a usable relative path, never "".
func TestDetectContentRootNeverReturnsEmpty(t *testing.T) {
	t.Setenv("CONTENT_ROOT", "")
	if got := DetectContentRoot(); got == "" {
		t.Fatal("DetectContentRoot returned an empty path; every join would then be absolute-ish garbage")
	}
}

// The result must not depend on the OS the server runs on.
//
// SanitizeAssetPath used filepath.ToSlash, which rewrites backslashes on Windows
// and does nothing on Linux. The value comes from an admin form filled on a
// Windows machine, so the same input produced a nested directory on a
// developer's box and one directory literally named "news\2026" on the Linux
// server. This test states the invariant rather than the platform: every
// expectation below is written without reference to GOOS, so it fails on
// whichever side drifts.
func TestSanitizeAssetPathIsPlatformIndependent(t *testing.T) {
	for in, want := range map[string]string{
		`news\2026`:        "news/2026",
		`news\2026\covers`: "news/2026/covers",
		`news/2026`:        "news/2026",
		`\news\`:           "news",
		`..\..\etc`:        "_/_/etc",
		`news\..\..\etc`:   "news/_/_/etc",
	} {
		if got := SanitizeAssetPath(in); got != want {
			t.Errorf("SanitizeAssetPath(%q) = %q, want %q", in, got, want)
		}
	}
}
