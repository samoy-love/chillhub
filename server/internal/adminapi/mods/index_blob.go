package mods

import (
	"bufio"
	"compress/gzip"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Index URLs are supplied by Thunderstore, but are still untrusted input.
// Validate redirects too: checking only the first request permits SSRF.
func (c *Client) allowedIndexURL(raw string) bool {
	u, err := url.Parse(raw)
	if err != nil || u.User != nil || u.Fragment != "" {
		return false
	}
	base, err := url.Parse(c.apiBase)
	if err == nil && u.Scheme == base.Scheme && u.Host == base.Host {
		return true
	}
	return u.Scheme == "https" &&
		(u.Host == "ccdn.thunderstore.io" || u.Host == "gcdn.thunderstore.io") &&
		strings.HasPrefix(u.Path, "/live/blob-storage/")
}

// fetchIndexBlob retries transport failures and rate limits, using the same
// pacing as metadata. Only one bounded chunk is held in memory at a time.
func (c *Client) fetchIndexBlob(ctx context.Context, raw string, limit int64) ([]byte, error) {
	if !c.allowedIndexURL(raw) {
		return nil, fmt.Errorf("mods: untrusted index URL %q", raw)
	}
	var lastErr error
	for attempt := 1; attempt <= maxAttempts; attempt++ {
		body, status, err := c.indexBlobOnce(ctx, raw, limit)
		if err == nil && status == http.StatusOK {
			c.lim.reward()
			return body, nil
		}
		if err == nil {
			err = fmt.Errorf("mods: index blob: status %d", status)
		}
		lastErr = err
		if status >= 400 && status < 500 && status != http.StatusTooManyRequests {
			break
		}
		if isRateLimited(status) {
			c.lim.penalize()
		}
		if attempt < maxAttempts {
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(c.backoff(attempt)):
			}
		}
	}
	return nil, fmt.Errorf("mods: index blob %s: %w", raw, lastErr)
}

func (c *Client) indexBlobOnce(ctx context.Context, raw string, limit int64) ([]byte, int, error) {
	release, err := c.acquire(ctx)
	if err != nil {
		return nil, 0, err
	}
	defer release()
	ctx, cancel := context.WithTimeout(ctx, requestTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, raw, nil)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("User-Agent", userAgent)
	req.Header.Set("Accept", "application/json")
	client := *c.http
	client.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		if !c.allowedIndexURL(req.URL.String()) || len(via) >= 10 {
			return errors.New("mods: rejected index redirect")
		}
		if c.http.CheckRedirect != nil {
			return c.http.CheckRedirect(req, via)
		}
		return nil
	}
	res, err := client.Do(req) // #nosec G704 -- URL and every redirect are allowlisted above.
	if err != nil {
		return nil, 0, err
	}
	defer func() { _ = res.Body.Close() }()
	if res.StatusCode != http.StatusOK {
		return nil, res.StatusCode, nil
	}
	// Blob storage serves gzip files without Content-Encoding. HTTP gzip may
	// also have been removed by Transport, so inspect the remaining bytes.
	reader := bufio.NewReader(res.Body)
	var body io.Reader = reader
	magic, _ := reader.Peek(2)
	if len(magic) == 2 && magic[0] == 0x1f && magic[1] == 0x8b {
		z, err := gzip.NewReader(reader)
		if err != nil {
			return nil, res.StatusCode, err
		}
		defer func() { _ = z.Close() }()
		body = z
	}
	data, err := io.ReadAll(io.LimitReader(body, limit+1))
	if int64(len(data)) > limit {
		return nil, res.StatusCode, fmt.Errorf("mods: index blob exceeds %d decoded bytes", limit)
	}
	return data, res.StatusCode, err
}
