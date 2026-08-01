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
