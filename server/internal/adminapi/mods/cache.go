package mods

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"

	"ChillHub/server/internal/adminutil"
)

// The archive cache is what makes a second build cheap.
//
// Modpacks for one game overlap heavily — they are drawn from the same few
// hundred popular mods — so rebuilding a neighbouring pack, or the same pack
// after a single mod updated, re-downloads almost nothing. Without it every
// rebuild of LethalReloaded is another 1.8 GB pulled from Thunderstore, and the
// migration set for Lethal Company is another 5.8 GB.
//
// Entries never need validating: a Thunderstore version is immutable, so
// "{namespace}-{name}-{version}.zip exists" is the whole check.

const (
	// CacheTTL is how long an unused archive is kept.
	//
	// Freshness is measured by LAST USE, not by download time: every cache hit
	// touches the file's modification time. Access time would be the natural
	// choice and is not usable — NTFS updates it lazily, and many systems have
	// it disabled outright.
	CacheTTL = 30 * 24 * time.Hour

	// cacheTmpPrefix marks a partial download. A build that dies mid-transfer
	// must not leave behind a truncated file under the real name, because the
	// existence check above would then treat it as a valid package forever.
	cacheTmpPrefix = ".partial-"

	// maxDownloadAttempts counts the first try plus retries of one archive.
	maxDownloadAttempts = 4

	// downloadRetryBase is the first backoff step; it grows with the attempt.
	downloadRetryBase = 2 * time.Second
)

// ArchiveCache stores downloaded package archives.
type ArchiveCache struct {
	dir string
	ttl time.Duration
}

// NewArchiveCache places the cache under the content root's tmp directory,
// which is already excluded from everything the public API serves.
func NewArchiveCache(contentRoot string) *ArchiveCache {
	return &ArchiveCache{
		dir: filepath.Join(contentRoot, "tmp", "ts-cache"),
		ttl: CacheTTL,
	}
}

// Dir is the cache directory.
func (c *ArchiveCache) Dir() string { return c.dir }

// path returns the cache location of one package. fullName comes from
// Thunderstore metadata, so it is validated before it becomes a file name.
func (c *ArchiveCache) path(fullName string) (string, error) {
	if !safeFullName(fullName) {
		return "", fmt.Errorf("mods: unsafe package name %q", fullName)
	}
	return filepath.Join(c.dir, fullName+".zip"), nil
}

// safeFullName accepts only what a Thunderstore full name can contain. It is
// the guard that keeps a hostile package name from becoming a path.
func safeFullName(s string) bool {
	if s == "" || len(s) > 200 {
		return false
	}
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		case r == '-' || r == '_' || r == '.':
		default:
			return false
		}
	}
	// A name made only of dots would be "." or ".." with an extension appended
	// on some paths; refuse the whole class rather than reason about it.
	return !strings.HasPrefix(s, ".")
}

// Fetch returns the local path of a package archive, downloading it if needed.
// hit reports whether the cache already had it.
func (c *ArchiveCache) Fetch(ctx context.Context, client *Client, fullName string) (path string, hit bool, err error) {
	p, err := c.path(fullName)
	if err != nil {
		return "", false, err
	}
	if st, err := os.Stat(p); err == nil && st.Size() > 0 {
		// Touch so the sweeper measures time since last USE.
		now := time.Now()
		if err := os.Chtimes(p, now, now); err != nil {
			log.Printf("[mods] touch cached %s: %v", fullName, err)
		}
		return p, true, nil
	}

	if err := os.MkdirAll(c.dir, 0o755); err != nil { // #nosec G301 -- tmp tree
		return "", false, err
	}

	if err := downloadWithRetry(ctx, client, fullName, c.dir, p); err != nil {
		return "", false, err
	}
	return p, false, nil
}

// downloadWithRetry writes one package archive to dest, restarting from scratch
// on failure.
//
// The retry lives HERE and not in Client.Download because only this side owns
// the destination file: a body that died halfway has already been written, and
// resuming would silently produce a corrupt archive. Truncating and starting
// over is the only correct repair, and only the owner of the file may do it.
//
// Without this, one dropped connection anywhere in a 151-package, 1.8 GB build
// throws away every byte downloaded so far — while the small metadata requests
// beside it retry four times with backoff. The expensive half of the pipeline
// had no resilience at all.
func downloadWithRetry(ctx context.Context, client *Client, fullName, dir, dest string) error {
	var lastErr error
	for attempt := 1; attempt <= maxDownloadAttempts; attempt++ {
		tmp := filepath.Join(dir, cacheTmpPrefix+adminutil.GenID())
		err := downloadOnce(ctx, client, fullName, tmp, dest)
		if err == nil {
			return nil
		}
		_ = os.Remove(tmp)

		// A package Thunderstore no longer serves will still be gone on the
		// fourth attempt; only transport failures are worth repeating.
		if errors.Is(err, ErrNotFound) || ctx.Err() != nil {
			return err
		}
		lastErr = err
		log.Printf("[mods] download %s failed (attempt %d/%d): %v", fullName, attempt, maxDownloadAttempts, err)

		if attempt < maxDownloadAttempts {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(time.Duration(attempt) * downloadRetryBase):
			}
		}
	}
	return fmt.Errorf("mods: downloading %s: %w", fullName, lastErr)
}

// downloadOnce streams the archive into tmp and renames it onto dest.
func downloadOnce(ctx context.Context, client *Client, fullName, tmp, dest string) error {
	f, err := os.Create(tmp) // #nosec G304 -- tmp is cacheDir plus a generated id
	if err != nil {
		return err
	}
	n, dlErr := client.Download(ctx, fullName, f)
	closeErr := f.Close()
	if dlErr != nil {
		return dlErr
	}
	if closeErr != nil {
		return closeErr
	}
	if n == 0 {
		return fmt.Errorf("mods: %s downloaded as an empty file", fullName)
	}
	return os.Rename(tmp, dest)
}

// Stats reports how much the cache holds.
func (c *ArchiveCache) Stats() (files int, bytes int64) {
	entries, err := os.ReadDir(c.dir)
	if err != nil {
		return 0, 0
	}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		files++
		bytes += info.Size()
	}
	return files, bytes
}

// Sweep deletes archives untouched for longer than the TTL, plus any partial
// downloads left by a killed build. Returns how many files went and how many
// bytes that freed.
func (c *ArchiveCache) Sweep() (removed int, freed int64) {
	return c.sweepOlderThan(time.Now().Add(-c.ttl))
}

// Clear empties the cache regardless of age; the panel offers it as a button
// for when the disk matters more than the next rebuild.
func (c *ArchiveCache) Clear() (removed int, freed int64) {
	return c.sweepOlderThan(time.Now().Add(time.Hour))
}

func (c *ArchiveCache) sweepOlderThan(cutoff time.Time) (removed int, freed int64) {
	entries, err := os.ReadDir(c.dir)
	if err != nil {
		return 0, 0
	}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		partial := strings.HasPrefix(e.Name(), cacheTmpPrefix)
		// A partial file is junk the moment nothing is writing it. An hour of
		// grace keeps a sweep from deleting a download that is still running.
		if !partial && info.ModTime().After(cutoff) {
			continue
		}
		if partial && info.ModTime().After(time.Now().Add(-time.Hour)) {
			continue
		}
		p := filepath.Join(c.dir, e.Name())
		if err := os.Remove(p); err != nil {
			log.Printf("[mods] sweep %s: %v", e.Name(), err)
			continue
		}
		removed++
		freed += info.Size()
	}
	if removed > 0 {
		log.Printf("[mods] cache sweep removed %d files, freed %.1f MB", removed, float64(freed)/(1<<20))
	}
	return removed, freed
}
