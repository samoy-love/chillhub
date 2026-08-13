// Package thunderstore lets the admin panel search Thunderstore.io mod
// communities and download a mod (with its full dependency graph) into a
// modpack profile served under content/<gameId>/modpacks/<namespace>-<name>/.
//
// # Resolving the dependency graph while downloading
//
// PLAN.md asks for two things that look contradictory at first: "the graph
// must be fully resolved before any download starts" and "a node's own
// dependencies are read from manifest.json inside its just-downloaded zip,
// not from a fresh API call". The two are reconciled by splitting "resolved"
// from "merged into the live profile":
//
//   - The ROOT package's dependency list comes from one call to the
//     experimental package-detail API (PackageDetail) — this is the "first
//     network request" PLAN.md refers to.
//   - Every other node in the graph is discovered by downloading its zip to a
//     TEMPORARY directory and reading manifest.json there; its own
//     dependencies (for the next recursion level) come from that manifest,
//     never from a second API call.
//   - Nothing is written under content/<gameId>/modpacks/... — the live,
//     served profile — until the whole graph has been walked this way and
//     every node's temp download has succeeded. Only then does mergeGraph
//     copy BepInEx/config and BepInEx/plugins from each node's temp dir into
//     the final profile directory.
//
// So "resolved before download starts" is true of the PUBLISHED profile: it
// only ever sees a complete, verified graph. "Resolved" for an individual
// non-root node is unavoidably entangled with downloading it, because that is
// the only place its own manifest.json lives — the temp download IS the
// resolution step for that node, but it is not yet a merge.
package thunderstore

import (
	"archive/zip"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Tunables. Named constants so tests and callers can see the actual budget
// rather than a bare number scattered through the code.
const (
	// MaxZipBytes caps one package's downloaded zip. Modpacks routinely run
	// into the hundreds of megabytes (whole BepInEx plugin trees, textures,
	// audio), so this is deliberately far above media.MaxImageBytes (32 MiB).
	MaxZipBytes = 500 << 20 // 500 MiB

	// MaxGraphNodes caps the number of distinct namespace-name-version nodes
	// a single resolve may visit, so a pathological or malicious dependency
	// chain aborts with a clear error instead of hammering Thunderstore with
	// hundreds of requests.
	MaxGraphNodes = 200

	// MaxGraphBytes caps the cumulative size of every zip downloaded while
	// resolving one graph.
	MaxGraphBytes = 3 << 30 // 3 GiB

	// listCacheTTL is how long a community's package list is cached in memory.
	listCacheTTL = 5 * time.Minute

	// requestTimeout bounds every single HTTP call made to thunderstore.io.
	requestTimeout = 20 * time.Second
)

// Sentinel errors surfaced to the admin UI. Kept distinct from fmt.Errorf
// wrapping so callers (and tests) can tell them apart with errors.Is.
var (
	ErrTooManyNodes  = errors.New("dependency graph too large (too many packages)")
	ErrGraphTooLarge = errors.New("dependency graph too large (cumulative download size)")
	ErrZipTooLarge   = errors.New("package zip exceeds the size limit")
	ErrZipSlip       = errors.New("zip entry escapes the extraction directory")
	ErrNotFound      = errors.New("modpack not found")
	ErrUnreachable   = errors.New("thunderstore.io unreachable")
	ErrBadDependency = errors.New("malformed dependency reference")
)

// cleanupNames are the service files stripped from a package's own temp
// directory before it is merged into the profile — never BepInEx/config or
// BepInEx/plugins. Comparison is case-insensitive: Thunderstore authors are
// not consistent about "README.md" vs "readme.md".
var cleanupNames = map[string]bool{
	"changelog.md":  true,
	"icon.png":      true,
	"manifest.json": true,
	"readme.md":     true,
	"license":       true,
	"license.txt":   true,
}

// PackageSummary is one row of a community's package list, as shown to the
// admin panel's search box.
type PackageSummary struct {
	Namespace       string `json:"namespace"`
	Name            string `json:"name"`
	FullName        string `json:"fullName"`
	Description     string `json:"description"`
	IconURL         string `json:"iconUrl"`
	LatestVersion   string `json:"latestVersion"`
	Downloads       int    `json:"downloads"`
	ThunderstoreURL string `json:"thunderstoreUrl"`
}

// PackageRef identifies one exact package version.
type PackageRef struct {
	Namespace string `json:"namespace"`
	Name      string `json:"name"`
	Version   string `json:"version"`
}

func (p PackageRef) key() string { return p.Namespace + "-" + p.Name + "-" + p.Version }

// ModFileOrigin records which package placed one file under BepInEx/ — needed
// to remove exactly that package's files (and no one else's) on a future
// update.
type ModFileOrigin struct {
	Path    string `json:"path"`    // slash-separated, relative to BepInEx/
	Package string `json:"package"` // "namespace-name-version"
}

// ModpackMeta is meta.json, stored next to BepInEx/ in the profile directory.
type ModpackMeta struct {
	Root      PackageRef      `json:"root"`
	Graph     []PackageRef    `json:"graph"`     // every node the resolve visited, root included
	Files     []ModFileOrigin `json:"files"`     // every merged file under BepInEx/, with its origin
	UpdatedAt string          `json:"updatedAt"` // RFC3339
}

// --- Thunderstore HTTP client -------------------------------------------------

// tsListItemVersion is one entry of a package's "versions" array in the
// community package-list API.
type tsListItemVersion struct {
	VersionNumber string `json:"version_number"`
	Description   string `json:"description"`
	Icon          string `json:"icon"`
	Downloads     int    `json:"downloads"`
}

// tsListItem is one package in the community package-list API
// (GET /c/<community>/api/v1/package/).
type tsListItem struct {
	Name     string              `json:"name"`
	FullName string              `json:"full_name"`
	Owner    string              `json:"owner"`
	Versions []tsListItemVersion `json:"versions"`
}

// tsPackageDetail is the shape of the experimental package-detail API
// (GET /api/experimental/package/<namespace>/<name>/) that this package
// actually reads from: the latest version's number and its dependency list.
type tsPackageDetail struct {
	Latest struct {
		VersionNumber string   `json:"version_number"`
		Dependencies  []string `json:"dependencies"`
	} `json:"latest"`
}

// tsManifest is manifest.json inside a downloaded package zip. Confirmed (per
// PLAN.md) to carry the same "dependencies" shape as the experimental API.
type tsManifest struct {
	Name          string   `json:"name"`
	VersionNumber string   `json:"version_number"`
	Dependencies  []string `json:"dependencies"`
}

// client is the network surface this package needs from Thunderstore. It
// exists so tests can supply a fake and never touch the real network.
type client interface {
	ListCommunityPackages(ctx context.Context, community string) ([]tsListItem, error)
	PackageDetail(ctx context.Context, namespace, name string) (*tsPackageDetail, error)
	// DownloadZip returns the raw zip bytes of one package version, capped at
	// MaxZipBytes+1 (the extra byte is how the caller tells "exactly at the
	// limit" from "over it").
	DownloadZip(ctx context.Context, namespace, name, version string) ([]byte, error)
}

// httpClient is the real client, talking to thunderstore.io over HTTPS.
//
// thunderstore.io is a fixed, trusted host (not attacker-supplied like the
// news "upload by URL" field), so the SSRF dialer media.go uses for arbitrary
// URLs is not needed here — but every call still carries a timeout and every
// response body is still read through a capped reader, so an unreachable or
// slow-drip host cannot wedge a request handler indefinitely.
type httpClient struct {
	hc *http.Client
}

func newHTTPClient() *httpClient {
	return &httpClient{hc: &http.Client{Timeout: requestTimeout}}
}

const thunderstoreBase = "https://thunderstore.io"

// maxListBytes caps the community package-list JSON. A large community can
// list thousands of packages; this is generous but not unbounded.
const maxListBytes = 64 << 20 // 64 MiB

func (c *httpClient) getJSON(ctx context.Context, url string, maxBytes int64, out any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	resp, err := c.hc.Do(req)
	if err != nil {
		return fmt.Errorf("%w: %v", ErrUnreachable, err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode == http.StatusNotFound {
		return ErrNotFound
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("%w: thunderstore returned %d", ErrUnreachable, resp.StatusCode)
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
	if err != nil {
		return fmt.Errorf("%w: %v", ErrUnreachable, err)
	}
	if int64(len(b)) > maxBytes {
		return fmt.Errorf("response exceeds %d bytes", maxBytes)
	}
	return json.Unmarshal(b, out)
}

func (c *httpClient) ListCommunityPackages(ctx context.Context, community string) ([]tsListItem, error) {
	url := thunderstoreBase + "/c/" + community + "/api/v1/package/"
	var out []tsListItem
	if err := c.getJSON(ctx, url, maxListBytes, &out); err != nil {
		return nil, err
	}
	return out, nil
}

func (c *httpClient) PackageDetail(ctx context.Context, namespace, name string) (*tsPackageDetail, error) {
	url := thunderstoreBase + "/api/experimental/package/" + namespace + "/" + name + "/"
	var out tsPackageDetail
	if err := c.getJSON(ctx, url, 4<<20, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *httpClient) DownloadZip(ctx context.Context, namespace, name, version string) ([]byte, error) {
	url := thunderstoreBase + "/package/download/" + namespace + "/" + name + "/" + version + "/"
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	resp, err := c.hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrUnreachable, err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode == http.StatusNotFound {
		return nil, ErrNotFound
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("%w: thunderstore returned %d", ErrUnreachable, resp.StatusCode)
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, MaxZipBytes+1))
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrUnreachable, err)
	}
	if int64(len(b)) > MaxZipBytes {
		return nil, ErrZipTooLarge
	}
	return b, nil
}

// --- Search cache --------------------------------------------------------------

type listCacheEntry struct {
	items   []tsListItem
	expires time.Time
}

type listCache struct {
	mu      sync.Mutex
	entries map[string]listCacheEntry
}

func newListCache() *listCache { return &listCache{entries: map[string]listCacheEntry{}} }

func (c *listCache) get(community string) ([]tsListItem, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	e, ok := c.entries[community]
	if !ok || time.Now().After(e.expires) {
		return nil, false
	}
	return e.items, true
}

func (c *listCache) put(community string, items []tsListItem) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.entries[community] = listCacheEntry{items: items, expires: time.Now().Add(listCacheTTL)}
}

// --- Handlers ------------------------------------------------------------------

// Handlers serves the Thunderstore endpoints for one content root.
type Handlers struct {
	root   string
	client client
	cache  *listCache
}

// New returns handlers rooted at the given content directory, talking to the
// real thunderstore.io.
func New(root string) *Handlers {
	return &Handlers{root: root, client: newHTTPClient(), cache: newListCache()}
}

// newForTest builds Handlers around a fake client, for unit tests that must
// not touch the network.
func newForTest(root string, c client) *Handlers {
	return &Handlers{root: root, client: c, cache: newListCache()}
}

func (h *Handlers) modpacksDir(gameID string) string {
	return filepath.Join(h.root, "content", gameID, "modpacks")
}

// SearchPackages returns the packages of one community whose name or
// description contains query (case-insensitive), using the ~5 minute cache so
// a typing admin does not re-fetch the whole community list on every
// keystroke.
func (h *Handlers) SearchPackages(ctx context.Context, community, query string) ([]PackageSummary, error) {
	items, ok := h.cache.get(community)
	if !ok {
		fetched, err := h.client.ListCommunityPackages(ctx, community)
		if err != nil {
			return nil, err
		}
		items = fetched
		h.cache.put(community, items)
	}
	q := strings.ToLower(strings.TrimSpace(query))
	out := make([]PackageSummary, 0, len(items))
	for _, it := range items {
		if len(it.Versions) == 0 {
			continue
		}
		latest := it.Versions[0]
		if q != "" && !strings.Contains(strings.ToLower(it.Name), q) &&
			!strings.Contains(strings.ToLower(latest.Description), q) &&
			!strings.Contains(strings.ToLower(it.Owner), q) {
			continue
		}
		out = append(out, PackageSummary{
			Namespace:       it.Owner,
			Name:            it.Name,
			FullName:        it.FullName,
			Description:     latest.Description,
			IconURL:         latest.Icon,
			LatestVersion:   latest.VersionNumber,
			Downloads:       latest.Downloads,
			ThunderstoreURL: thunderstorePageURL(it.Owner, it.Name),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Downloads > out[j].Downloads })
	return out, nil
}

func thunderstorePageURL(namespace, name string) string {
	return thunderstoreBase + "/package/" + namespace + "/" + name + "/"
}

// parseDependency splits a Thunderstore dependency reference
// "<namespace>-<name>-<version>" into its three parts. The name itself may
// contain hyphens, but the version is always the last dash-separated segment
// and the namespace is always the first, which is enough to split reliably.
func parseDependency(s string) (namespace, name, version string, err error) {
	parts := strings.Split(s, "-")
	if len(parts) < 3 {
		return "", "", "", fmt.Errorf("%w: %q", ErrBadDependency, s)
	}
	namespace = parts[0]
	version = parts[len(parts)-1]
	name = strings.Join(parts[1:len(parts)-1], "-")
	if namespace == "" || name == "" || version == "" {
		return "", "", "", fmt.Errorf("%w: %q", ErrBadDependency, s)
	}
	return namespace, name, version, nil
}

// resolvedNode is one downloaded-and-extracted graph node, still sitting in a
// temp directory, not yet merged into the live profile.
type resolvedNode struct {
	ref     PackageRef
	tempDir string
}

// resolver walks the dependency graph, downloading each node into its own
// temp directory. Nothing it touches is inside the served content tree.
type resolver struct {
	h          *Handlers
	onProgress func(string)
	visited    map[string]bool
	nodes      []resolvedNode
	totalBytes int64
}

// visit downloads and extracts one node (if not already visited), then
// recurses into its dependencies. deps, when non-nil, is used instead of
// re-reading the node's own manifest — this is how the root node's
// dependencies (known from the experimental API) are threaded in without a
// redundant manifest read.
func (rs *resolver) visit(ctx context.Context, ref PackageRef, depsHint []string) error {
	key := ref.key()
	// Dedup + cycle protection: mark visited BEFORE recursing, so a diamond
	// dependency (two parents needing the same child) downloads the child
	// exactly once, and a cycle (A -> B -> A) terminates instead of recursing
	// forever.
	if rs.visited[key] {
		return nil
	}
	rs.visited[key] = true

	if len(rs.visited) > MaxGraphNodes {
		return ErrTooManyNodes
	}

	rs.progress(fmt.Sprintf("резолвим %s...", key))
	zipBytes, err := rs.h.client.DownloadZip(ctx, ref.Namespace, ref.Name, ref.Version)
	if err != nil {
		return fmt.Errorf("скачивание %s: %w", key, err)
	}
	rs.totalBytes += int64(len(zipBytes))
	if rs.totalBytes > MaxGraphBytes {
		return ErrGraphTooLarge
	}

	tempDir, err := os.MkdirTemp("", "chillhub-modpack-"+sanitizeTempComponent(key)+"-")
	if err != nil {
		return err
	}
	if err := extractZip(zipBytes, tempDir); err != nil {
		return fmt.Errorf("распаковка %s: %w", key, err)
	}

	deps := depsHint
	if deps == nil {
		deps, err = readManifestDependencies(tempDir)
		if err != nil {
			return fmt.Errorf("чтение manifest.json для %s: %w", key, err)
		}
	}

	removed, err := cleanupServiceFiles(tempDir)
	if err != nil {
		return fmt.Errorf("очистка служебных файлов %s: %w", key, err)
	}
	if len(removed) > 0 {
		rs.progress(fmt.Sprintf("%s: удалены служебные файлы: %s", key, strings.Join(removed, ", ")))
	}

	rs.nodes = append(rs.nodes, resolvedNode{ref: ref, tempDir: tempDir})
	rs.progress(fmt.Sprintf("%d/%d скачан: %s", len(rs.nodes), len(rs.visited), key))

	for _, depStr := range deps {
		dns, dname, dver, perr := parseDependency(depStr)
		if perr != nil {
			log.Printf("[thunderstore] пропускаю некорректную зависимость %q: %v", depStr, perr)
			continue
		}
		if err := rs.visit(ctx, PackageRef{Namespace: dns, Name: dname, Version: dver}, nil); err != nil {
			return err
		}
	}
	return nil
}

func (rs *resolver) progress(msg string) {
	if rs.onProgress != nil {
		rs.onProgress(msg)
	}
}

func (rs *resolver) cleanupTempDirs() {
	for _, n := range rs.nodes {
		_ = os.RemoveAll(n.tempDir)
	}
}

func sanitizeTempComponent(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteByte('_')
		}
	}
	return b.String()
}

// readManifestDependencies reads dependencies from manifest.json in dir.
func readManifestDependencies(dir string) ([]string, error) {
	p := filepath.Join(dir, "manifest.json")
	b, err := os.ReadFile(p) // #nosec G304 -- dir is a temp dir this package created and extracted into.
	if err != nil {
		return nil, err
	}
	var m tsManifest
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, err
	}
	return m.Dependencies, nil
}

// cleanupServiceFiles removes the known service files from dir (case
// insensitive), scoped to dir alone — this runs once per node, right after
// that node's own zip was extracted, never against the merged profile tree.
// Returns the names actually removed, for the progress log.
func cleanupServiceFiles(dir string) ([]string, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	var removed []string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		if !cleanupNames[strings.ToLower(e.Name())] {
			continue
		}
		if err := os.Remove(filepath.Join(dir, e.Name())); err != nil {
			return removed, err
		}
		removed = append(removed, e.Name())
	}
	sort.Strings(removed)
	return removed, nil
}

// extractZip extracts zip bytes into dir, refusing any entry that would
// resolve outside dir (zip-slip) or that is an absolute path or a symlink.
func extractZip(zipBytes []byte, dir string) error {
	r, err := zip.NewReader(bytesReaderAt(zipBytes), int64(len(zipBytes)))
	if err != nil {
		return err
	}
	for _, f := range r.File {
		if err := extractOneEntry(f, dir); err != nil {
			return err
		}
	}
	return nil
}

func extractOneEntry(f *zip.File, dir string) error {
	name := f.Name
	if filepath.IsAbs(name) || strings.HasPrefix(name, "/") || strings.HasPrefix(name, "\\") {
		return fmt.Errorf("%w: %q", ErrZipSlip, name)
	}
	// Symlinks can point outside dir and are never something a mod's own
	// files legitimately need; skip them rather than let os.Symlink create
	// one that later reads escape through it.
	if f.Mode()&os.ModeSymlink != 0 {
		return nil
	}
	target := filepath.Join(dir, filepath.FromSlash(name))
	if !adminutil.EnsureWithin(dir, target) {
		return fmt.Errorf("%w: %q", ErrZipSlip, name)
	}
	if f.FileInfo().IsDir() || strings.HasSuffix(name, "/") {
		return os.MkdirAll(target, 0o755) // #nosec G301 -- temp extraction dir, not served.
	}
	if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil { // #nosec G301
		return err
	}
	rc, err := f.Open()
	if err != nil {
		return err
	}
	defer func() { _ = rc.Close() }()
	out, err := os.OpenFile(target, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o644) // #nosec G304,G302 -- target checked by EnsureWithin above.
	if err != nil {
		return err
	}
	defer func() { _ = out.Close() }()
	// The zip's own uncompressed-size header is not trusted for the loop
	// bound; the copy is capped independently so a crafted entry cannot
	// zip-bomb this into filling the disk.
	if _, err := io.Copy(out, io.LimitReader(rc, MaxZipBytes)); err != nil {
		return err
	}
	return nil
}

// bytesReaderAt adapts a byte slice to io.ReaderAt for zip.NewReader.
type bytesReaderAt []byte

func (b bytesReaderAt) ReadAt(p []byte, off int64) (int, error) {
	if off >= int64(len(b)) {
		return 0, io.EOF
	}
	n := copy(p, b[off:])
	if n < len(p) {
		return n, io.EOF
	}
	return n, nil
}

// --- Merge into the live profile ------------------------------------------------

// mergeGraph copies BepInEx/config and BepInEx/plugins from every resolved
// node into the profile directory, logging (not silently overwriting) any
// filename collision between mods, and returns the file origins for
// meta.json.
func mergeGraph(profileDir string, nodes []resolvedNode, onProgress func(string)) ([]ModFileOrigin, error) {
	var origins []ModFileOrigin
	seen := map[string]string{} // relative path (under BepInEx/) -> owning package key
	for _, n := range nodes {
		key := n.ref.key()
		for _, sub := range []string{filepath.Join("BepInEx", "config"), filepath.Join("BepInEx", "plugins")} {
			src := filepath.Join(n.tempDir, sub)
			if _, err := os.Stat(src); err != nil {
				continue // this package does not carry that subtree
			}
			err := filepath.Walk(src, func(p string, info os.FileInfo, err error) error {
				if err != nil {
					return err
				}
				if info.IsDir() {
					return nil
				}
				rel, _ := filepath.Rel(filepath.Join(n.tempDir, "BepInEx"), p)
				rel = filepath.ToSlash(rel)
				dest := filepath.Join(profileDir, "BepInEx", filepath.FromSlash(rel))
				if !adminutil.EnsureWithin(profileDir, dest) {
					return fmt.Errorf("%w: %q", ErrZipSlip, rel)
				}
				if owner, exists := seen[rel]; exists {
					msg := fmt.Sprintf("конфликт файлов: %s уже добавлен пакетом %s, %s тоже его несёт — оставлен первый", rel, owner, key)
					log.Printf("[thunderstore] %s", msg)
					if onProgress != nil {
						onProgress(msg)
					}
					return nil
				}
				if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil { // #nosec G301
					return err
				}
				data, err := os.ReadFile(p) // #nosec G304 -- p comes from filepath.Walk over a temp dir this package extracted.
				if err != nil {
					return err
				}
				if err := adminutil.WriteFileAtomic(dest, data, 0o644); err != nil {
					return err
				}
				seen[rel] = key
				origins = append(origins, ModFileOrigin{Path: rel, Package: key})
				return nil
			})
			if err != nil {
				return nil, err
			}
		}
	}
	sort.Slice(origins, func(i, j int) bool { return origins[i].Path < origins[j].Path })
	return origins, nil
}

// --- Public entry point ---------------------------------------------------------

// DownloadModpack resolves the full dependency graph of namespace/name at
// version, downloads every node, and — only once the whole graph is known —
// merges every node's BepInEx/config and BepInEx/plugins into
// content/<gameId>/modpacks/<namespace>-<name>/BepInEx/. onProgress is called
// with a short human-readable step description after each meaningful step
// (never a single overall percentage).
func (h *Handlers) DownloadModpack(ctx context.Context, gameID, namespace, name, version string, onProgress func(string)) error {
	if onProgress == nil {
		onProgress = func(string) {}
	}
	root := PackageRef{Namespace: namespace, Name: name, Version: version}

	// The root's dependency list is the one place this resolve calls the
	// experimental detail API — see the package doc comment.
	onProgress("получаю сведения о пакете " + root.key() + "...")
	detail, err := h.client.PackageDetail(ctx, namespace, name)
	if err != nil {
		return fmt.Errorf("получение сведений о %s: %w", root.key(), err)
	}

	rs := &resolver{h: h, onProgress: onProgress, visited: map[string]bool{}}
	if err := rs.visit(ctx, root, detail.Latest.Dependencies); err != nil {
		rs.cleanupTempDirs()
		return err
	}
	defer rs.cleanupTempDirs()

	onProgress(fmt.Sprintf("граф зависимостей разрешён: %d пакет(ов), начинаю объединение...", len(rs.nodes)))

	profileDir := filepath.Join(h.modpacksDir(gameID), namespace+"-"+name)
	if err := os.MkdirAll(profileDir, 0o755); err != nil { // #nosec G301 -- served by nginx like the rest of content/.
		return err
	}
	files, err := mergeGraph(profileDir, rs.nodes, onProgress)
	if err != nil {
		return fmt.Errorf("объединение BepInEx: %w", err)
	}

	graph := make([]PackageRef, 0, len(rs.nodes))
	for _, n := range rs.nodes {
		graph = append(graph, n.ref)
	}
	meta := ModpackMeta{
		Root:      root,
		Graph:     graph,
		Files:     files,
		UpdatedAt: time.Now().UTC().Format(time.RFC3339),
	}
	metaBytes, err := json.MarshalIndent(meta, "", "  ")
	if err != nil {
		return err
	}
	if err := adminutil.WriteFileAtomic(filepath.Join(profileDir, "meta.json"), metaBytes, 0o644); err != nil {
		return err
	}
	onProgress(fmt.Sprintf("готово: %d файл(ов) в BepInEx/", len(files)))
	return nil
}

// --- Listing / deleting downloaded modpacks --------------------------------------

// DownloadedModpack is one entry of ListDownloaded.
type DownloadedModpack struct {
	Namespace       string       `json:"namespace"`
	Name            string       `json:"name"`
	RootVersion     string       `json:"rootVersion"`
	Graph           []PackageRef `json:"graph"`
	FileCount       int          `json:"fileCount"`
	UpdatedAt       string       `json:"updatedAt"`
	ThunderstoreURL string       `json:"thunderstoreUrl"`
}

// ListDownloaded returns the modpack profiles already downloaded for gameID.
func (h *Handlers) ListDownloaded(gameID string) ([]DownloadedModpack, error) {
	base := h.modpacksDir(gameID)
	entries, err := os.ReadDir(base)
	if err != nil {
		if os.IsNotExist(err) {
			return []DownloadedModpack{}, nil
		}
		return nil, err
	}
	out := make([]DownloadedModpack, 0, len(entries))
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		meta, err := readModpackMeta(filepath.Join(base, e.Name()))
		if err != nil {
			log.Printf("[thunderstore] пропускаю %s: %v", e.Name(), err)
			continue
		}
		out = append(out, DownloadedModpack{
			Namespace:       meta.Root.Namespace,
			Name:            meta.Root.Name,
			RootVersion:     meta.Root.Version,
			Graph:           meta.Graph,
			FileCount:       len(meta.Files),
			UpdatedAt:       meta.UpdatedAt,
			ThunderstoreURL: thunderstorePageURL(meta.Root.Namespace, meta.Root.Name),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out, nil
}

func readModpackMeta(dir string) (ModpackMeta, error) {
	b, err := os.ReadFile(filepath.Join(dir, "meta.json")) // #nosec G304 -- dir comes from ReadDir(modpacksDir(gameID)).
	if err != nil {
		return ModpackMeta{}, err
	}
	var m ModpackMeta
	if err := json.Unmarshal(b, &m); err != nil {
		return ModpackMeta{}, err
	}
	return m, nil
}

// DeleteModpack removes a downloaded modpack profile entirely (meta.json plus
// the merged BepInEx/ tree), reporting the files that were removed.
func (h *Handlers) DeleteModpack(gameID, namespace, name string) ([]string, error) {
	base := h.modpacksDir(gameID)
	dir := filepath.Join(base, namespace+"-"+name)
	if !adminutil.EnsureWithin(base, dir) {
		return nil, fmt.Errorf("invalid modpack id")
	}
	meta, err := readModpackMeta(dir)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrNotFound, err)
	}
	removed := make([]string, 0, len(meta.Files))
	for _, f := range meta.Files {
		removed = append(removed, f.Path)
	}
	if err := os.RemoveAll(dir); err != nil {
		return nil, err
	}
	sort.Strings(removed)
	return removed, nil
}

// --- HTTP handlers ---------------------------------------------------------------

func gameIDFromRequest(r *http.Request) string {
	return strings.TrimSpace(r.URL.Query().Get("gameId"))
}

// Search handles GET /admin/thunderstore/search?community=..&q=..
func (h *Handlers) Search(w http.ResponseWriter, r *http.Request) {
	community := strings.TrimSpace(r.URL.Query().Get("community"))
	if community == "" {
		http.Error(w, "missing community", http.StatusBadRequest)
		return
	}
	items, err := h.SearchPackages(r.Context(), community, r.URL.Query().Get("q"))
	if err != nil {
		h.failNetwork(w, err)
		return
	}
	adminutil.WriteJSON(w, struct {
		Items []PackageSummary `json:"items"`
	}{Items: items})
}

// List handles GET /admin/thunderstore/list?gameId=..
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	gid := gameIDFromRequest(r)
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	items, err := h.ListDownloaded(gid)
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to list modpacks", "thunderstore", err)
		return
	}
	adminutil.WriteJSON(w, struct {
		Items []DownloadedModpack `json:"items"`
	}{Items: items})
}

// Delete handles POST /admin/thunderstore/delete {gameId, namespace, name}
func (h *Handlers) Delete(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	var body struct {
		GameID    string `json:"gameId"`
		Namespace string `json:"namespace"`
		Name      string `json:"name"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeGameID(body.GameID) || body.Namespace == "" || body.Name == "" {
		http.Error(w, "invalid gameId, namespace or name", http.StatusBadRequest)
		return
	}
	removed, err := h.DeleteModpack(body.GameID, body.Namespace, body.Name)
	if err != nil {
		if errors.Is(err, ErrNotFound) {
			http.Error(w, "modpack not found", http.StatusNotFound)
			return
		}
		adminutil.Fail(w, http.StatusInternalServerError, "failed to delete modpack", "thunderstore", err)
		return
	}
	adminutil.WriteJSON(w, struct {
		Status       string   `json:"status"`
		RemovedFiles []string `json:"removedFiles"`
	}{Status: "ok", RemovedFiles: removed})
}

// Download handles POST /admin/thunderstore/download
// {gameId, namespace, name, version} and streams NDJSON progress lines of the
// form {"type":"progress","message":"..."} and, on completion or failure,
// {"type":"done", ...} or {"type":"error","message":"..."}.
//
// Streaming (rather than one final JSON response) is what lets the panel show
// each resolve/download/merge step as it happens instead of a single spinner
// for a request that can run for minutes.
func (h *Handlers) Download(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	var body struct {
		GameID    string `json:"gameId"`
		Namespace string `json:"namespace"`
		Name      string `json:"name"`
		Version   string `json:"version"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeGameID(body.GameID) || body.Namespace == "" || body.Name == "" || body.Version == "" {
		http.Error(w, "invalid gameId, namespace, name or version", http.StatusBadRequest)
		return
	}

	w.Header().Set("Content-Type", "application/x-ndjson")
	w.WriteHeader(http.StatusOK)
	fl := adminutil.FlusherFor(w)
	emit := func(format string, a ...any) {
		_, _ = fmt.Fprintf(w, format, a...)
		fl.Flush()
	}
	onProgress := func(msg string) {
		b, _ := json.Marshal(msg)
		emit("{\"type\":\"progress\",\"message\":%s}\n", string(b))
	}

	err := h.DownloadModpack(r.Context(), body.GameID, body.Namespace, body.Name, body.Version, onProgress)
	if err != nil {
		b, _ := json.Marshal(err.Error())
		emit("{\"type\":\"error\",\"message\":%s}\n", string(b))
		return
	}
	emit("{\"type\":\"done\"}\n")
}

// failNetwork answers a request that failed before any streaming started,
// with a status that distinguishes "thunderstore.io is unreachable" from an
// ordinary server error.
func (h *Handlers) failNetwork(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, ErrUnreachable):
		http.Error(w, "не удалось связаться с thunderstore.io: "+err.Error(), http.StatusBadGateway)
	case errors.Is(err, ErrNotFound):
		http.Error(w, "не найдено", http.StatusNotFound)
	default:
		adminutil.Fail(w, http.StatusInternalServerError, "thunderstore request failed", "thunderstore", err)
	}
}
