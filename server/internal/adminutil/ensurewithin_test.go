package adminutil

import (
	"path/filepath"
	"testing"
)

func TestEnsureWithinRejectsEscapes(t *testing.T) {
	base := filepath.Join(t.TempDir(), "assets")
	bad := []string{
		filepath.Join(base, ".."),
		filepath.Join(base, "..", "secret.txt"),
		filepath.Join(base, "sub", "..", "..", "secret.txt"),
	}
	for _, p := range bad {
		if EnsureWithin(base, p) {
			t.Errorf("EnsureWithin(%q) = true, want false", p)
		}
	}
}

// BASE ITSELF COUNTS AS "INSIDE". This is the function's most surprising
// property and it has already cost the project a near-miss.
//
// SanitizeFilename keeps a lone ".", so a delete request naming "." resolved the
// target to the gallery ROOT, and EnsureWithin waved it through: os.RemoveAll
// would have taken out every news cover, landing-page picture and game icon in
// one unauthenticated-looking request, with no undo. The fix lives in the
// callers that destroy things (see isAssetsRoot in adminapi/news/assets.go),
// because the callers that merely LIST or CREATE inside the root legitimately
// need base to pass.
//
// The behaviour is pinned here so nobody "fixes" it centrally and silently
// breaks listing, and so the next person adding a destructive handler sees that
// EnsureWithin alone is not enough.
func TestEnsureWithinTreatsBaseItselfAsInside(t *testing.T) {
	base := filepath.Join(t.TempDir(), "assets")

	for _, p := range []string{base, base + string(filepath.Separator), filepath.Join(base, ".")} {
		if !EnsureWithin(base, p) {
			t.Fatalf("EnsureWithin(base, %q) = false; callers below depend on base passing", p)
		}
	}
}

// A file name that merely STARTS with two dots is an ordinary file inside base;
// the old prefix check rejected it along with real ".." traversal.
func TestEnsureWithinAllowsNamesStartingWithDots(t *testing.T) {
	base := filepath.Join(t.TempDir(), "assets")
	good := []string{
		filepath.Join(base, "..foo"),
		filepath.Join(base, "..gitkeep"),
		filepath.Join(base, "sub", "..bar.png"),
		filepath.Join(base, "normal.png"),
	}
	for _, p := range good {
		if !EnsureWithin(base, p) {
			t.Errorf("EnsureWithin(%q) = false, want true", p)
		}
	}
}
