package builds

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"ChillHub/server/internal/adminutil"
)

// Extraction must stop at the ceiling instead of writing until the volume is
// full: the entry sizes in a ZIP are attacker-controlled, so only the bytes
// actually produced can be trusted.
func TestUnzipStopsAtTheSizeCeiling(t *testing.T) {
	t.Setenv("BUILD_MAX_UNCOMPRESSED_BYTES", "1024")
	root := t.TempDir()
	h := New(root)

	big := strings.Repeat("A", 4096) // compresses to almost nothing
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{"big.bin": big}))
	if w.Code == http.StatusOK {
		t.Fatalf("oversized archive was accepted: %s", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Fatal("oversized archive got published")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// An archive that fits must still extract with the budget in place.
func TestUnzipAllowsArchivesUnderTheCeiling(t *testing.T) {
	t.Setenv("BUILD_MAX_UNCOMPRESSED_BYTES", "1048576")
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "hello"}))
	if w.Code != http.StatusOK {
		t.Fatalf("upload failed: %d %s", w.Code, w.Body.String())
	}
}

func TestExtractBudgetCountsAcrossEntries(t *testing.T) {
	b := adminutil.NewExtractBudget(10)
	if err := b.Copy(discard{}, strings.NewReader("12345")); err != nil {
		t.Fatalf("first entry: %v", err)
	}
	if err := b.Copy(discard{}, strings.NewReader("12345")); err != nil {
		t.Fatalf("second entry: %v", err)
	}
	if err := b.Copy(discard{}, strings.NewReader("x")); err == nil {
		t.Fatal("the byte past the budget must be rejected")
	}
}

type discard struct{}

func (discard) Write(p []byte) (int, error) { return len(p), nil }
