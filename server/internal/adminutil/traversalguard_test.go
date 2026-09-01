package adminutil

import (
	"path/filepath"
	"testing"
)

// A version label becomes a directory name under content/{gameId}/. "." names
// the game directory — every version of the game — and ".." names the content
// root, i.e. every game on the server. Both are dots and nothing else, so the
// character class alone accepted them, and the delete, upload and import
// endpoints joined them straight onto a path they then removed or renamed.
func TestIsSafeVersionRejectsTheTwoLabelsThatNameADirectoryAbove(t *testing.T) {
	for _, s := range []string{".", ".."} {
		if IsSafeVersion(s) {
			t.Errorf("IsSafeVersion(%q) = true; joined onto a game directory it points at the tree above", s)
		}
	}
	// Dots are still ordinary version characters — semver is the reason the
	// class allows them at all, and "..." is a plain directory name.
	for _, s := range []string{"1.0.0", "1.2.3-rc.1", "...", ".1", "1."} {
		if !IsSafeVersion(s) {
			t.Errorf("IsSafeVersion(%q) = false, it names a directory of its own", s)
		}
	}
}

// EnsureWithin answers "inside" for base itself, which is what the asset
// browser needs and what a delete must never accept: the path that collapses
// exactly onto its own base is the whole tree the caller meant to delete one
// item from.
func TestEnsureStrictlyWithinRefusesBaseItself(t *testing.T) {
	base := filepath.Join(t.TempDir(), "content", "game")

	for _, p := range []string{
		base,
		base + string(filepath.Separator),
		filepath.Join(base, "."),
		filepath.Join(base, "1.0.0", ".."),
	} {
		if EnsureStrictlyWithin(base, p) {
			t.Errorf("EnsureStrictlyWithin(base, %q) = true; that path IS base, and the caller is about to delete it", p)
		}
	}
}

// Everything EnsureWithin accepts below base must still pass, or the strict
// variant would refuse the ordinary version directory it is meant to guard.
func TestEnsureStrictlyWithinStillAcceptsRealChildren(t *testing.T) {
	base := filepath.Join(t.TempDir(), "content", "game")

	for _, p := range []string{
		filepath.Join(base, "1.0.0"),
		filepath.Join(base, "1.0.0", "files", "app.exe"),
		filepath.Join(base, "..foo"),
	} {
		if !EnsureStrictlyWithin(base, p) {
			t.Errorf("EnsureStrictlyWithin(base, %q) = false, want true", p)
		}
	}
	for _, p := range []string{
		filepath.Join(base, ".."),
		filepath.Join(base, "..", "other"),
		filepath.Join(base, "sub", "..", "..", "escape"),
	} {
		if EnsureStrictlyWithin(base, p) {
			t.Errorf("EnsureStrictlyWithin(base, %q) = true, it is outside base", p)
		}
	}
}
