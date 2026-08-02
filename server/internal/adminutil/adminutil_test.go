package adminutil

import (
	"os"
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

func TestIsHexID(t *testing.T) {
	for _, s := range []string{"0123456789abcdef0123456789abcdef", "DEADBEEFCAFE1234"} {
		if !IsHexID(s) {
			t.Errorf("%q must be accepted", s)
		}
	}
	for _, s := range []string{"", "..", "../../etc", "a/b", "zzzz", "abc", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0"} {
		if IsHexID(s) {
			t.Errorf("%q must be rejected", s)
		}
	}
	// Everything the server generates must pass.
	for range 50 {
		if !IsHexID(GenID()) || !IsHexID(NewBuildID()) {
			t.Fatal("a generated id was rejected")
		}
	}
}

// The replacement must be all-or-nothing: a reader either sees the old content
// or the new one, never a truncated file, and no temp file may be left behind.
func TestWriteFileAtomicReplacesWholeFile(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "state.json")
	if err := WriteFileAtomic(p, []byte(`{"v":1}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := WriteFileAtomic(p, []byte(`{"v":2}`), 0o644); err != nil {
		t.Fatalf("overwrite: %v", err)
	}
	b, err := os.ReadFile(p) // #nosec G304 -- p is built from t.TempDir(), not from any request.
	if err != nil {
		t.Fatal(err)
	}
	if string(b) != `{"v":2}` {
		t.Fatalf("content = %q", string(b))
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 {
		names := make([]string, 0, len(entries))
		for _, e := range entries {
			names = append(names, e.Name())
		}
		t.Fatalf("temp files left behind: %v", names)
	}
}

// It also creates missing parent directories, like the call sites expect.
func TestWriteFileAtomicCreatesParents(t *testing.T) {
	p := filepath.Join(t.TempDir(), "a", "b", "state.json")
	if err := WriteFileAtomic(p, []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(p); err != nil {
		t.Fatal(err)
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
