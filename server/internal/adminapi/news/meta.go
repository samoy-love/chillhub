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

// dirs is the pair of directories one news scope lives in.
//
// pub is served to the world: nginx (and the public API in dev) hand out
// everything under content/news verbatim, so ONLY published articles and the
// filtered index may be stored there.
//
// priv is under content/news_private, which no web server maps: drafts and
// news_meta.json — the map of which slugs are drafts — live there.
type dirs struct {
	pub  string
	priv string
}

// metaPath is the private metadata file. It must never be inside pub: it lists
// every slug together with its published flag, i.e. exactly the information
// that leaks the existence of unpublished articles.
func metaPath(d dirs) string { return filepath.Join(d.priv, "news_meta.json") }

// publicIndexPath is the index the launcher reads (published articles only).
func publicIndexPath(d dirs) string { return filepath.Join(d.pub, "index.json") }

// adminIndexPath is the full index (drafts included) the admin UI reads.
func adminIndexPath(d dirs) string { return filepath.Join(d.priv, "index.json") }

func readMeta(d dirs) map[string]meta {
	b, err := os.ReadFile(metaPath(d))
	if err != nil {
		// Content published before drafts were moved out of the public tree still
		// has its metadata next to the articles; read it so nothing is lost until
		// the next rebuild migrates the file.
		b, err = os.ReadFile(filepath.Join(d.pub, "news_meta.json"))
		if err != nil {
			return map[string]meta{}
		}
	}
	var m map[string]meta
	if json.Unmarshal(b, &m) != nil || m == nil {
		return map[string]meta{}
	}
	return m
}

func writeMeta(d dirs, m map[string]meta) error {
	b, _ := json.MarshalIndent(m, "", "  ")
	if err := os.MkdirAll(d.priv, 0o755); err != nil {
		return err
	}
	if err := os.WriteFile(metaPath(d), b, 0o644); err != nil {
		return err
	}
	// Drop any legacy copy from the public tree.
	_ = os.Remove(filepath.Join(d.pub, "news_meta.json"))
	return nil
}
