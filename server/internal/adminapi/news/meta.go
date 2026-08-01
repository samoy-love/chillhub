package news

import (
	"encoding/json"
	"os"
	"path/filepath"
)

// meta is the per-article record kept in news_meta.json alongside index.json,
// keyed by slug. Cover and published state live here rather than in the
// markdown so that a rebuild never infers them from the article body.
type meta struct {
	Published bool   `json:"published"`
	CoverUrl  string `json:"coverUrl"`
}

func metaPath(base string) string { return filepath.Join(base, "news_meta.json") }

func readMeta(base string) map[string]meta {
	b, err := os.ReadFile(metaPath(base))
	if err != nil {
		return map[string]meta{}
	}
	var m map[string]meta
	if json.Unmarshal(b, &m) != nil || m == nil {
		return map[string]meta{}
	}
	return m
}

func writeMeta(base string, m map[string]meta) error {
	b, _ := json.MarshalIndent(m, "", "  ")
	return os.WriteFile(metaPath(base), b, 0o644)
}
