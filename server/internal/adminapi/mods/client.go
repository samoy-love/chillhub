// Package mods builds Thunderstore modpacks into a ready-to-serve file tree.
//
// A Thunderstore "modpack" is not an archive of mods. It is an almost empty
// package whose manifest.json lists the exact versions of the mods it wants,
// and every one of those mods is a separate package with its own dependencies.
// Downloading ASTeam/LethalReloaded gives 9 MB containing no mods at all;
// the real pack is 151 packages and 1.8 GB once the tree is resolved.
//
// So this package does what r2modman and Thunderstore Mod Manager do, on the
// server, once, for everybody:
//
//	resolve the dependency tree -> download every package -> lay the files out
//	by the game's install rules -> drop the per-package clutter -> hand the
//	finished tree to builds.scanManifest for hashing and publication.
//
// The result is an ordinary Chill Hub version: the launcher syncs it with the
// same diffing, resuming, rollback and integrity machinery it already uses for
// game builds, and never talks to Thunderstore itself.
package mods

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const (
	// DefaultAPIBase is Thunderstore's API host.
	DefaultAPIBase = "https://thunderstore.io"

	// DefaultCDNBase serves the package archives. Their URLs are USUALLY
	// predictable — {namespace}-{name}-{version}.zip — which is why a build
	// tries this first and never pays for a redirect.
	//
	// «Usually» is not «always», and the exception cost a build. Thunderstore
	// stores the object under a name its storage layer chose, and for a long
	// package name that name is TRUNCATED with a random suffix appended:
	// MelanieMelicious_2_sToRy_ShIp__works_w_Wider_Ship_Mod-2.2.14 lives as
	// …_Wider_Ship_M_xvy048L.zip. The guessed URL then gets 403 AccessDenied
	// from the bucket — not 404 — and the whole build stopped on it. The
	// package's own download_url from the API is authoritative and is used as
	// the fallback; see archiveURLs.
	DefaultCDNBase = "https://gcdn.thunderstore.io/live/repository/packages"

	// maxParallel bounds concurrent METADATA connections. Throughput is decided
	// by the limiter below, not by this number; it only keeps the connection
	// pool and the number of half-finished requests small.
	maxParallel = 3

	// maxCDNParallel bounds concurrent ARCHIVE transfers.
	//
	// Archives come from gcdn.thunderstore.io — object storage, a different
	// host from the API and not the surface that answers a burst with 429.
	// Pacing them at the API's three requests a second was pure loss: 151
	// packages and 1.8 GB went out one at a time behind a 320 ms gap, and the
	// download — the only part of a build that is actually big — ran slower
	// than the metadata walk in front of it.
	maxCDNParallel = 6

	// cdnInterval keeps even the CDN from being hit as a burst. It is a
	// politeness floor, not a rate limit: six transfers in flight, each new one
	// starting at least this long after the previous.
	cdnInterval = 40 * time.Millisecond

	// baseInterval is the minimum gap between the START of two requests,
	// across every goroutine sharing the client.
	//
	// Measured against the live API, not guessed. 80 distinct package requests
	// at 8 concurrent workers (~38 req/s) came back 44 × 200 and 36 × 429; the
	// same requests paced to ~2.8 req/s came back 40/40 clean, repeatedly. The
	// value below is ~3 req/s, which resolves the largest real modpack (151
	// packages) in under a minute — irrelevant next to downloading its 1.8 GB.
	baseInterval = 320 * time.Millisecond

	// maxInterval caps the adaptive slowdown.
	maxInterval = 5 * time.Second

	// maxAttempts counts the first try plus retries.
	//
	// Rate limiting gets a longer budget than a plain error: a 429 says "later",
	// and giving up on it turns a temporary refusal into a failed build.
	maxAttempts    = 4
	maxAttempts429 = 7
	minCooldown    = 3 * time.Second
	maxCooldown    = 90 * time.Second

	// retryBase is the first backoff step; it grows exponentially.
	retryBase = 1200 * time.Millisecond

	// requestTimeout bounds a single metadata request. Downloads get their own,
	// much larger budget from the caller's context.
	requestTimeout = 30 * time.Second

	// maxMetadataBytes bounds a metadata response. Thunderstore's package
	// documents are a few kilobytes; the ecosystem schema is ~1.5 MB and is
	// fetched with its own limit.
	maxMetadataBytes = 8 << 20
)

// ErrNotFound reports a package or version that Thunderstore no longer serves.
//
// It is deliberately distinct from every other failure: a deleted mod is a
// fact about the modpack that the operator has to see and decide about, while
// a timeout is a fact about the network that should be retried. Collapsing the
// two is how an incomplete build gets published as if it were complete.
var ErrNotFound = errors.New("mods: package not found on thunderstore")

// Client talks to Thunderstore. The zero value is not usable; call NewClient.
type Client struct {
	apiBase string
	cdnBase string
	http    *http.Client

	// sem bounds in-flight METADATA requests across every goroutine sharing
	// the client; cdnSem does the same for archive transfers. They are separate
	// because the two hosts have nothing to do with each other: a download
	// queued behind three package lookups is waiting for no reason.
	sem    chan struct{}
	cdnSem chan struct{}

	// lim paces API requests and absorbs rate limiting; cdnLim does the same,
	// far more loosely, for the CDN. Separate state for the same reason: a
	// cooldown earned on the API must not throttle transfers that never
	// touched it.
	lim    *limiter
	cdnLim *limiter

	// metaDir caches version metadata on disk; empty disables it.
	metaDir string
}

// limiter spaces requests out and slows the whole client down when
// Thunderstore pushes back.
//
// It has to be global rather than per-request because the limit is enforced on
// the client's address: a goroutine that sleeps alone while its siblings keep
// firing has not reduced the load at all. Thunderstore sends no Retry-After —
// the refusal is a Cloudflare mitigation with an HTML body — so the cooldown
// is our own, doubling on each refusal and decaying once traffic flows again.
type limiter struct {
	mu       sync.Mutex
	next     time.Time     // earliest start time for the next request
	interval time.Duration // current spacing
	cooldown time.Duration // current penalty length, 0 when healthy
	ok       int           // consecutive successes since the last refusal

	// minCool/maxCool bound the penalty. They are fields rather than constants
	// so a test against a local server can run the same escalation logic in
	// milliseconds instead of the three minutes the production ladder takes.
	minCool time.Duration
	maxCool time.Duration
}

func newLimiter() *limiter {
	return &limiter{interval: baseInterval, minCool: minCooldown, maxCool: maxCooldown}
}

// wait blocks until this goroutine's turn.
func (l *limiter) wait(ctx context.Context) error {
	l.mu.Lock()
	now := time.Now()
	slot := l.next
	if slot.Before(now) {
		slot = now
	}
	l.next = slot.Add(l.interval)
	l.mu.Unlock()

	d := time.Until(slot)
	if d <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-t.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// penalize records a refusal: everything queued behind this point is pushed
// back by a growing cooldown, and the steady-state spacing widens.
func (l *limiter) penalize() time.Duration {
	l.mu.Lock()
	defer l.mu.Unlock()

	l.ok = 0
	if l.cooldown == 0 {
		l.cooldown = l.minCool
	} else if l.cooldown < l.maxCool {
		l.cooldown *= 2
		if l.cooldown > l.maxCool {
			l.cooldown = l.maxCool
		}
	}
	if l.interval < maxInterval {
		l.interval += l.interval / 2
		if l.interval > maxInterval {
			l.interval = maxInterval
		}
	}
	if resume := time.Now().Add(l.cooldown); resume.After(l.next) {
		l.next = resume
	}
	return l.cooldown
}

// reward decays the penalty after a run of successes, so one bad minute does
// not leave the client crawling for the rest of a long build.
func (l *limiter) reward() {
	l.mu.Lock()
	defer l.mu.Unlock()

	l.ok++
	if l.ok < 20 {
		return
	}
	l.ok = 0
	l.cooldown = 0
	if l.interval > baseInterval {
		l.interval -= l.interval / 4
		if l.interval < baseInterval {
			l.interval = baseInterval
		}
	}
}

// snapshot reports the current spacing; used by tests and the build log.
func (l *limiter) snapshot() time.Duration {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.interval
}

// NewClient returns a client with the production endpoints and a bounded
// connection pool. Pass a non-nil hc to override the transport in tests.
func NewClient(hc *http.Client) *Client {
	if hc == nil {
		hc = &http.Client{
			Timeout: 0, // per-request deadlines come from the context
			Transport: &http.Transport{
				MaxIdleConns:        (maxParallel + maxCDNParallel) * 2,
				MaxIdleConnsPerHost: (maxParallel + maxCDNParallel) * 2,
				IdleConnTimeout:     90 * time.Second,
			},
		}
	}
	return &Client{
		apiBase: DefaultAPIBase,
		cdnBase: DefaultCDNBase,
		http:    hc,
		sem:     make(chan struct{}, maxParallel),
		cdnSem:  make(chan struct{}, maxCDNParallel),
		lim:     newLimiter(),
		cdnLim:  &limiter{interval: cdnInterval, minCool: minCooldown, maxCool: maxCooldown},
	}
}

// WithMetaCache stores version metadata under dir.
//
// A published Thunderstore version is immutable — that is the same fact the
// archive cache rests on — so the document describing it can be kept forever
// and the second build of a pack skips the entire dependency walk. Without it
// rebuilding LethalReloaded after one mod updated costs 151 API requests and
// the minute of pacing that goes with them, every time.
func (c *Client) WithMetaCache(dir string) *Client {
	c.metaDir = dir
	return c
}

// WithBases points the client at different hosts. Used by tests against an
// httptest server; production always uses the defaults.
func (c *Client) WithBases(apiBase, cdnBase string) *Client {
	if apiBase != "" {
		c.apiBase = strings.TrimRight(apiBase, "/")
	}
	if cdnBase != "" {
		c.cdnBase = strings.TrimRight(cdnBase, "/")
	}
	return c
}

// WithInterval overrides the request spacing.
//
// Only for tests against a local server: the production value is calibrated
// against Thunderstore's actual rate limit and lowering it there is how a
// build starts losing packages to 429s.
func (c *Client) WithInterval(d time.Duration) *Client {
	c.cdnLim.mu.Lock()
	c.cdnLim.interval = d
	c.cdnLim.minCool = d * 4
	c.cdnLim.maxCool = d * 40
	c.cdnLim.mu.Unlock()

	c.lim.mu.Lock()
	c.lim.interval = d
	// Scale the penalty ladder with the spacing, or a test that provokes a 503
	// would sit through the full production escalation (3s doubling to 90s,
	// three minutes in total) to assert a single error.
	c.lim.minCool = d * 4
	c.lim.maxCool = d * 40
	c.lim.mu.Unlock()
	return c
}

// PackageVersion is one published version of a Thunderstore package.
type PackageVersion struct {
	Namespace     string   `json:"namespace"`
	Name          string   `json:"name"`
	VersionNumber string   `json:"version_number"`
	FullName      string   `json:"full_name"`
	Description   string   `json:"description"`
	Icon          string   `json:"icon"`
	Dependencies  []string `json:"dependencies"`
	DownloadURL   string   `json:"download_url"`
	WebsiteURL    string   `json:"website_url"`
	IsActive      bool     `json:"is_active"`
}

// Package is a Thunderstore package with its newest version inlined.
type Package struct {
	Namespace    string         `json:"namespace"`
	Name         string         `json:"name"`
	FullName     string         `json:"full_name"`
	Owner        string         `json:"owner"`
	PackageURL   string         `json:"package_url"`
	DateUpdated  string         `json:"date_updated"`
	IsDeprecated bool           `json:"is_deprecated"`
	Latest       PackageVersion `json:"latest"`
}

// GetVersion fetches one exact version. A modpack pins every dependency, so
// this is the call the resolver makes for all but the root package.
func (c *Client) GetVersion(ctx context.Context, ns, name, version string) (*PackageVersion, error) {
	full := ns + "-" + name + "-" + version
	if v := c.metaRead(full); v != nil {
		return v, nil
	}
	var v PackageVersion
	url := fmt.Sprintf("%s/api/experimental/package/%s/%s/%s/", c.apiBase, ns, name, version)
	if err := c.getJSON(ctx, url, &v); err != nil {
		return nil, err
	}
	c.metaWrite(full, &v)
	return &v, nil
}

// metaRead returns a cached version document, or nil.
//
// A corrupt or unreadable entry is silently a miss: the network answer is
// authoritative and cheap enough that failing the build over a bad cache file
// would be the wrong trade.
func (c *Client) metaRead(fullName string) *PackageVersion {
	p := c.metaPath(fullName)
	if p == "" {
		return nil
	}
	b, err := os.ReadFile(p) // #nosec G304 -- metaPath validates the name
	if err != nil {
		return nil
	}
	var v PackageVersion
	if err := json.Unmarshal(b, &v); err != nil || v.FullName == "" {
		return nil
	}
	// Touch so the cache sweeper measures time since last use, exactly as it
	// does for archives.
	now := time.Now()
	if err := os.Chtimes(p, now, now); err != nil {
		log.Printf("[mods] touch cached metadata %s: %v", fullName, err)
	}
	return &v
}

func (c *Client) metaWrite(fullName string, v *PackageVersion) {
	p := c.metaPath(fullName)
	if p == "" {
		return
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil { // #nosec G301 -- tmp tree
		log.Printf("[mods] metadata cache dir: %v", err)
		return
	}
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	// Write-then-rename: a half-written document read by a parallel build would
	// come back as a miss at best, and the point of the cache is to be boring.
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil { // #nosec G306 -- tmp tree
		log.Printf("[mods] cache metadata %s: %v", fullName, err)
		return
	}
	if err := os.Rename(tmp, p); err != nil {
		_ = os.Remove(tmp)
		log.Printf("[mods] cache metadata %s: %v", fullName, err)
	}
}

func (c *Client) metaPath(fullName string) string {
	if c.metaDir == "" || !safeFullName(fullName) {
		return ""
	}
	return filepath.Join(c.metaDir, fullName+".json")
}

// GetPackage fetches a package and its newest version. Used when the operator
// picks a modpack (the catalog listing carries no version number) and by the
// update check.
func (c *Client) GetPackage(ctx context.Context, ns, name string) (*Package, error) {
	var p Package
	url := fmt.Sprintf("%s/api/experimental/package/%s/%s/", c.apiBase, ns, name)
	if err := c.getJSON(ctx, url, &p); err != nil {
		return nil, err
	}
	return &p, nil
}

// GetReadme returns the rendered README markdown of one version.
func (c *Client) GetReadme(ctx context.Context, ns, name, version string) (string, error) {
	var doc struct {
		Markdown string `json:"markdown"`
	}
	url := fmt.Sprintf("%s/api/experimental/package/%s/%s/%s/readme/", c.apiBase, ns, name, version)
	if err := c.getJSON(ctx, url, &doc); err != nil {
		return "", err
	}
	return doc.Markdown, nil
}

// ArchiveURL returns the CDN URL of a package archive. The naming scheme is
// stable and every version is immutable, which is what makes the on-disk cache
// safe to keep without any validation beyond the file existing.
// An empty string means the name is not one a package can have. The name
// comes from Thunderstore metadata rather than from an operator, but it still
// ends up in a URL and in a file path, and one guard for both is how those two
// stay in agreement about what is acceptable.
func (c *Client) ArchiveURL(fullName string) string {
	if !safeFullName(fullName) {
		return ""
	}
	return c.cdnBase + "/" + fullName + ".zip"
}

// ArchiveRef names one archive to transfer: the immutable full name the CDN
// URL is guessed from, and the package's own download link from the API, used
// when that guess turns out to be wrong.
type ArchiveRef struct {
	FullName    string
	DownloadURL string
}

// Ref is the archive reference of a resolved package.
func (p ResolvedPackage) Ref() ArchiveRef {
	return ArchiveRef{FullName: p.FullName, DownloadURL: p.DownloadURL}
}

// errWrongURL marks «this address does not serve the archive» — as opposed to
// «the transfer broke». Only the first is worth trying another address for.
var errWrongURL = errors.New("mods: archive is not at this url")

// archiveURLs lists where one archive may be fetched from, best first.
//
// The guessed CDN name goes first: it skips the API host entirely and so is
// neither rate limited nor redirected. The package's own download_url is the
// fallback, and it is the authoritative answer — Thunderstore truncates long
// object names and appends a random suffix, and no amount of guessing finds
// those.
//
// The fallback is checked against this client's own hosts before it becomes an
// outbound request. It arrives inside API metadata rather than from an
// operator, but «came from JSON we fetched» is not the same as «safe to fetch».
func (c *Client) archiveURLs(ref ArchiveRef) ([]string, error) {
	var urls []string
	if u := c.ArchiveURL(ref.FullName); u != "" {
		urls = append(urls, u)
	}
	if u := ref.DownloadURL; u != "" && u != urls0(urls) &&
		(strings.HasPrefix(u, c.apiBase+"/") || strings.HasPrefix(u, c.cdnBase+"/")) {
		urls = append(urls, u)
	}
	if len(urls) == 0 {
		return nil, fmt.Errorf("mods: no usable download url for %q", ref.FullName)
	}
	return urls, nil
}

func urls0(u []string) string {
	if len(u) == 0 {
		return ""
	}
	return u[0]
}

// acquireForURL takes a slot on whichever queue the host belongs to.
func (c *Client) acquireForURL(ctx context.Context, url string) (func(), error) {
	if strings.HasPrefix(url, c.cdnBase) {
		return c.acquireCDN(ctx)
	}
	return c.acquire(ctx)
}

// Download streams one package archive into w and returns the byte count.
//
// It does NOT retry a broken transfer: a partial body already written to w
// cannot be un-written, and the caller — which owns the destination file — is
// the only party that can truncate and start over. downloadWithRetry in
// cache.go does exactly that.
//
// Trying the SECOND address after the first answered with a wrong status is a
// different thing and does happen here: nothing was written, so nothing is at
// risk, and the alternative is a build that stops on a package whose only sin
// is a long name.
func (c *Client) Download(ctx context.Context, ref ArchiveRef, w io.Writer) (int64, error) {
	urls, err := c.archiveURLs(ref)
	if err != nil {
		return 0, err
	}
	var lastErr error
	for _, u := range urls {
		n, err := c.downloadFrom(ctx, ref.FullName, u, w)
		if err == nil {
			return n, nil
		}
		lastErr = err
		// A body already started cannot be replaced by another address.
		if !errors.Is(err, errWrongURL) || n > 0 {
			return n, err
		}
	}
	return 0, lastErr
}

func (c *Client) downloadFrom(ctx context.Context, fullName, url string, w io.Writer) (int64, error) {
	release, err := c.acquireForURL(ctx, url)
	if err != nil {
		return 0, err
	}
	defer release()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return 0, err
	}
	req.Header.Set("User-Agent", userAgent)

	res, err := c.http.Do(req)
	if err != nil {
		return 0, err
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		return 0, statusProblem("download", fullName, url, res.StatusCode)
	}
	return io.Copy(w, res.Body)
}

// statusProblem turns a status into the right kind of failure.
//
// 404 and 403 both mean «not here»: the CDN answers a guessed-wrong object
// name with 403 AccessDenied from the bucket, not with 404. Both therefore
// send the caller to the next address, and only when the LAST address says so
// is the package really gone. Everything else is a transport-shaped failure
// and is worth repeating at the same address.
func statusProblem(what, fullName, url string, code int) error {
	if code == http.StatusNotFound || code == http.StatusForbidden {
		return fmt.Errorf("%w (%s %s: %d)", errWrongURL, what, url, code)
	}
	return fmt.Errorf("mods: %s %s: unexpected status %d", what, fullName, code)
}

// ArchiveSize asks the CDN how big a package is without downloading it. The
// estimate is what lets a build refuse to start when the disk cannot hold the
// result — 1.8 GB is a bad thing to discover halfway through.
func (c *Client) ArchiveSize(ctx context.Context, ref ArchiveRef) (int64, error) {
	urls, err := c.archiveURLs(ref)
	if err != nil {
		return 0, err
	}
	var lastErr error
	for _, u := range urls {
		n, err := c.sizeFrom(ctx, ref.FullName, u)
		if err == nil {
			return n, nil
		}
		lastErr = err
		if !errors.Is(err, errWrongURL) {
			return 0, err
		}
	}
	return 0, fmt.Errorf("%w: %s (%w)", ErrNotFound, ref.FullName, lastErr)
}

func (c *Client) sizeFrom(ctx context.Context, fullName, url string) (int64, error) {
	release, err := c.acquireForURL(ctx, url)
	if err != nil {
		return 0, err
	}
	defer release()

	reqCtx, cancel := context.WithTimeout(ctx, requestTimeout)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodHead, url, nil)
	if err != nil {
		return 0, err
	}
	req.Header.Set("User-Agent", userAgent)

	res, err := c.http.Do(req)
	if err != nil {
		return 0, err
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		return 0, statusProblem("size", fullName, url, res.StatusCode)
	}
	return res.ContentLength, nil
}

// userAgent identifies the launcher's server to Thunderstore. A blank or
// default Go agent is the kind of thing that gets rate limited first.
const userAgent = "ChillHub-Launcher/1.0 (+https://launcher.samoy.love)"

// getJSON performs a bounded, retried GET and decodes the body.
//
// 404 short-circuits to ErrNotFound without burning retries: a deleted package
// will still be deleted on the fourth attempt.
func (c *Client) getJSON(ctx context.Context, url string, out any) error {
	var lastErr error
	attempts := maxAttempts
	for attempt := 1; attempt <= attempts; attempt++ {
		body, status, err := c.getOnce(ctx, url)
		switch {
		case err == nil && status == http.StatusOK:
			c.lim.reward()
			if err := json.Unmarshal(body, out); err != nil {
				return fmt.Errorf("mods: decode %s: %w", url, err)
			}
			return nil
		case status == http.StatusNotFound:
			// A deleted package will still be deleted on the fourth attempt.
			return fmt.Errorf("%w: %s", ErrNotFound, url)
		case isRateLimited(status):
			// Being told to slow down is not the same kind of failure as a
			// broken request: it gets its own, longer budget, and the penalty
			// applies to the WHOLE client so the other goroutines back off too.
			attempts = maxAttempts429
			cooldown := c.lim.penalize()
			lastErr = fmt.Errorf("mods: GET %s: rate limited (status %d)", url, status)
			log.Printf("[mods] rate limited on %s, backing off %s (spacing now %s)",
				url, cooldown.Round(time.Millisecond), c.lim.snapshot())
			// The limiter already pushed every queued request back by the
			// cooldown, so no extra sleep is needed here.
			if ctx.Err() != nil {
				return ctx.Err()
			}
			continue
		case err != nil:
			lastErr = err
		default:
			lastErr = fmt.Errorf("mods: GET %s: status %d", url, status)
		}

		if ctx.Err() != nil {
			return ctx.Err()
		}
		if attempt < attempts {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(c.backoff(attempt)):
			}
		}
	}
	log.Printf("[mods] giving up on %s after %d attempts: %v", url, attempts, lastErr)
	return lastErr
}

// backoff grows exponentially with a deterministic spread, so a level of 149
// sibling requests that all failed at once do not retry in lockstep.
func (c *Client) backoff(attempt int) time.Duration {
	// Tied to the limiter's own ladder so the test override scales this too.
	c.lim.mu.Lock()
	ceiling := c.lim.maxCool
	base := retryBase
	if c.lim.interval < baseInterval {
		base = c.lim.interval * 2
	}
	c.lim.mu.Unlock()

	return min(base<<(attempt-1), ceiling)
}

// getOnce is a single attempt. It returns the body and status separately so
// getJSON can tell "404, stop" from "503, retry" without parsing an error.
func (c *Client) getOnce(ctx context.Context, url string) ([]byte, int, error) {
	release, err := c.acquire(ctx)
	if err != nil {
		return nil, 0, err
	}
	defer release()

	reqCtx, cancel := context.WithTimeout(ctx, requestTimeout)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, url, nil)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("User-Agent", userAgent)
	req.Header.Set("Accept", "application/json")

	res, err := c.http.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		// Drain a little so the connection can be reused, then report.
		_, _ = io.CopyN(io.Discard, res.Body, 4<<10)
		return nil, res.StatusCode, nil
	}
	body, err := io.ReadAll(io.LimitReader(res.Body, maxMetadataBytes))
	if err != nil {
		return nil, res.StatusCode, err
	}
	return body, res.StatusCode, nil
}

// acquire takes a slot from the semaphore and waits for the limiter. The
// returned function releases the slot and must always be called.
func (c *Client) acquire(ctx context.Context) (func(), error) {
	select {
	case c.sem <- struct{}{}:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	// The wait happens INSIDE the slot so that the semaphore also bounds how
	// many goroutines are sitting on timers, and the limiter alone decides the
	// rate at which requests actually start.
	if err := c.lim.wait(ctx); err != nil {
		<-c.sem
		return nil, err
	}
	return func() { <-c.sem }, nil
}

// acquireCDN is acquire for archive transfers: its own slots, its own pacing.
func (c *Client) acquireCDN(ctx context.Context) (func(), error) {
	select {
	case c.cdnSem <- struct{}{}:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	if err := c.cdnLim.wait(ctx); err != nil {
		<-c.cdnSem
		return nil, err
	}
	return func() { <-c.cdnSem }, nil
}

// isRateLimited reports whether a status means "you are going too fast".
//
// Thunderstore fronts its API with Cloudflare, whose mitigation answers 429
// with an HTML body and no Retry-After. 503 is treated the same way: it is
// also a "come back later", and the handling is identical.
func isRateLimited(status int) bool {
	return status == http.StatusTooManyRequests || status == http.StatusServiceUnavailable
}

// SplitDependency splits a Thunderstore dependency string
// ("Author-Mod_Name-1.2.3") into its parts.
//
// The separator is ambiguous by design of the format: the name itself may
// contain hyphens, so only the FIRST and LAST segments are fixed. Splitting on
// the first two hyphens — the obvious reading — corrupts every package whose
// name contains one, and there are plenty.
func SplitDependency(dep string) (ns, name, version string, ok bool) {
	parts := strings.Split(strings.TrimSpace(dep), "-")
	if len(parts) < 3 {
		return "", "", "", false
	}
	ns = parts[0]
	version = parts[len(parts)-1]
	name = strings.Join(parts[1:len(parts)-1], "-")
	if ns == "" || name == "" || version == "" {
		return "", "", "", false
	}
	return ns, name, version, true
}

// PackageKey is the identity of a package without its version. The resolver
// keys on this so that two dependency strings asking for different versions of
// the same mod resolve to one installed copy.
func PackageKey(ns, name string) string {
	return strings.ToLower(ns + "-" + name)
}
