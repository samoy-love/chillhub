package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// listItem mirrors the shape ListVersions now returns. The admin table shows
// every field of it: without them the rows read "1.3.5 — —" and answer nothing.
type listItem struct {
	Version   string `json:"version"`
	CreatedAt string `json:"createdAt"`
	Files     int    `json:"files"`
	Bytes     int64  `json:"bytes"`
}

func listVersionStats(t *testing.T, h *Handlers, gid string) []listItem {
	t.Helper()
	w := httptest.NewRecorder()
	h.ListVersions(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/list?gameId="+gid, nil))
	if w.Code != http.StatusOK {
		t.Fatalf("list: %d %s", w.Code, w.Body.String())
	}
	var out struct {
		Items  []listItem `json:"items"`
		Latest string     `json:"latest"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	return out.Items
}

func TestListVersionsReportsSizeAndFileCount(t *testing.T) {
	h := New(t.TempDir())
	if _, _, err := h.writeManifest(manifest{
		Version:   "1.0.0",
		GameID:    "game",
		CreatedAt: "2026-08-01T10:00:00Z",
		Files: []manifestFile{
			{Path: "a.exe", Size: 100, Blake3: "aa"},
			{Path: "b.dll", Size: 250, Blake3: "bb"},
		},
	}, true); err != nil {
		t.Fatal(err)
	}

	items := listVersionStats(t, h, "game")
	if len(items) != 1 {
		t.Fatalf("got %d items, want 1", len(items))
	}
	got := items[0]
	if got.Files != 2 {
		t.Errorf("Files = %d, want 2", got.Files)
	}
	if got.Bytes != 350 {
		t.Errorf("Bytes = %d, want 350", got.Bytes)
	}
	if got.CreatedAt != "2026-08-01T10:00:00Z" {
		t.Errorf("CreatedAt = %q, want the manifest value", got.CreatedAt)
	}
}

// A manifest that cannot be parsed must cost its own row's numbers and nothing
// else: the versions list is a convenience view, and blanking the whole table
// over one bad file would hide the versions that are fine.
func TestListVersionsSurvivesBrokenManifest(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	if _, _, err := h.writeManifest(manifest{
		Version: "1.0.0",
		GameID:  "game",
		Files:   []manifestFile{{Path: "a.exe", Size: 5, Blake3: "aa"}},
	}, true); err != nil {
		t.Fatal(err)
	}
	broken := filepath.Join(h.manifestsDir("game"), "9.9.9.json")
	if err := os.WriteFile(broken, []byte("{not json"), contentFilePerm); err != nil {
		t.Fatal(err)
	}

	items := listVersionStats(t, h, "game")
	if len(items) != 2 {
		t.Fatalf("got %d items, want both versions listed", len(items))
	}
	for _, it := range items {
		switch it.Version {
		case "1.0.0":
			if it.Files != 1 || it.Bytes != 5 {
				t.Errorf("good manifest lost its stats: %+v", it)
			}
		case "9.9.9":
			if it.Files != 0 || it.Bytes != 0 || it.CreatedAt != "" {
				t.Errorf("broken manifest should yield zeros, got %+v", it)
			}
		default:
			t.Errorf("unexpected version %q", it.Version)
		}
	}
}

func TestManifestStatsOnMissingFile(t *testing.T) {
	created, files, bytes := manifestStats(filepath.Join(t.TempDir(), "nope.json"))
	if created != "" || files != 0 || bytes != 0 {
		t.Fatalf("got %q/%d/%d, want zero values", created, files, bytes)
	}
}
