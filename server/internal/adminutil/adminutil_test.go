package adminutil

import (
	"path/filepath"
	"testing"
)

// traversalSlugs are slugs that must never be turned into a filesystem path.
var traversalSlugs = []string{
	"../../../pwned",
	"..\\..\\pwned",
	"../secret",
	"a/../../b",
	"/etc/passwd",
	"..",
	".hidden",
	"sub/dir",
}

func TestIsSafeNewsSlug(t *testing.T) {
	for _, s := range traversalSlugs {
		if IsSafeNewsSlug(s) {
			t.Errorf("slug %q must be rejected", s)
		}
	}
	if IsSafeNewsSlug("") {
		t.Error("empty slug must be rejected")
	}
	// legitimate slugs produced by the admin UI, including Cyrillic ones
	for _, s := range []string{"patch-1", "release_2024", "v1.2.3", "новость-1"} {
		if !IsSafeNewsSlug(s) {
			t.Errorf("slug %q must be accepted", s)
		}
	}
}

func TestNewsSlugPathRejectsTraversal(t *testing.T) {
	base := filepath.Join(t.TempDir(), "news")
	for _, s := range traversalSlugs {
		if p, err := NewsSlugPath(base, s); err == nil {
			t.Errorf("slug %q accepted, resolved to %q", s, p)
		}
	}
}
