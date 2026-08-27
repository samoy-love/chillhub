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

	"ChillHub/server/internal/adminutil"
)

// The ecosystem schema is Thunderstore's own answer to "where does this game
// live and where do its mod files go". One 1.5 MB document describes 326
// games: the Steam app id, the executable names, the folder name (which is not
// always the Steam install dir — How to Fish nests one level deeper), the mod
// loader, and the install rules that decide whether a file belongs in
// BepInEx/plugins, BepInEx/config or BepInEx/monomod.
//
// Reading it means the layout engine is DATA-driven: one implementation covers
// the 235 BepInEx games without a line of per-game code, and adding a game to
// the panel is typing its slug rather than editing Go.
const (
	// ecosystemURL is the published schema.
	ecosystemURL = DefaultAPIBase + "/api/experimental/schema/dev/latest/"

	// ecosystemTTL is how long a cached copy is served without asking again.
	// The schema changes when a game is added, which is not something a build
	// running right now needs to notice.
	ecosystemTTL = 6 * time.Hour

	// maxEcosystemBytes bounds the download. The live document is ~1.5 MB.
	maxEcosystemBytes = 32 << 20
)

// Ecosystem is the subset of the schema this server uses.
type Ecosystem struct {
	SchemaVersion     string             `json:"schemaVersion"`
	Games             map[string]EcoGame `json:"games"`
	ModloaderPackages []ModloaderPackage `json:"modloaderPackages"`
}

// EcoGame is one game entry.
type EcoGame struct {
	UUID          string         `json:"uuid"`
	Label         string         `json:"label"`
	Meta          EcoMeta        `json:"meta"`
	Distributions []Distribution `json:"distributions"`
	R2modman      []R2modmanDef  `json:"r2modman"`
}

// EcoMeta carries the display name.
type EcoMeta struct {
	DisplayName string `json:"displayName"`
	IconURL     string `json:"iconUrl"`
}

// Distribution names a store and the game's identifier there.
type Distribution struct {
	Platform   string `json:"platform"`
	Identifier string `json:"identifier"`
}

// R2modmanDef is the modding definition: where the game is installed and how
// package files are laid out inside it.
type R2modmanDef struct {
	InternalFolderName string         `json:"internalFolderName"`
	DataFolderName     string         `json:"dataFolderName"`
	SettingsIdentifier string         `json:"settingsIdentifier"`
	SteamFolderName    string         `json:"steamFolderName"`
	ExeNames           []string       `json:"exeNames"`
	PackageLoader      string         `json:"packageLoader"`
	GameInstanceType   string         `json:"gameInstanceType"`
	Distributions      []Distribution `json:"distributions"`
	InstallRules       []InstallRule  `json:"installRules"`
}

// InstallRule maps a folder inside a package archive to a folder in the game.
//
// trackingMethod is the load-bearing field:
//
//	subdir             files go to <route>/<Author>-<ModName>/, nested folders flattened
//	subdir-no-flatten  same, but the nested structure is preserved
//	none               files go straight to <route> with no per-mod folder (this is
//	                   what puts every mod's config in one BepInEx/config)
//	state              files are copied loose and recorded in a side file
//	package-zip        the archive itself is the payload
type InstallRule struct {
	Route                 string        `json:"route"`
	DefaultFileExtensions []string      `json:"defaultFileExtensions"`
	TrackingMethod        string        `json:"trackingMethod"`
	SubRoutes             []InstallRule `json:"subRoutes"`
	IsDefaultLocation     bool          `json:"isDefaultLocation"`
}

// ModloaderPackage identifies a package that IS the mod loader rather than a
// mod, and names the folder inside its archive whose contents belong at the
// root of the game.
//
// This mapping cannot be inferred: BepInEx arrives as BepInExPack/ in one
// package and BepInExPack_Valheim/ in another, and a modpack may pull the
// loader in transitively — Enhanced_HowToFish does not list BepInEx among its
// 18 direct dependencies at all.
type ModloaderPackage struct {
	PackageID  string `json:"packageId"`
	RootFolder string `json:"rootFolder"`
	Loader     string `json:"loader"`
}

// Def returns the modding definition of a game, or false when the schema has
// the game listed but not moddable.
func (g EcoGame) Def() (R2modmanDef, bool) {
	if len(g.R2modman) == 0 {
		return R2modmanDef{}, false
	}
	return g.R2modman[0], true
}

// SteamAppID returns the Steam identifier from the modding definition, falling
// back to the game-level distributions.
func (g EcoGame) SteamAppID() string {
	if def, ok := g.Def(); ok {
		if id := steamID(def.Distributions); id != "" {
			return id
		}
	}
	return steamID(g.Distributions)
}

func steamID(ds []Distribution) string {
	for _, d := range ds {
		if d.Platform == "steam" || d.Platform == "steam-direct" {
			return d.Identifier
		}
	}
	return ""
}

// LoaderRoot reports the root folder of a mod-loader package, and whether the
// package is a mod loader at all. The comparison is case-insensitive because
// dependency strings and schema ids disagree on casing in practice.
func (e *Ecosystem) LoaderRoot(ns, name string) (string, bool) {
	want := strings.ToLower(ns + "-" + name)
	for _, m := range e.ModloaderPackages {
		if strings.ToLower(m.PackageID) == want {
			return m.RootFolder, true
		}
	}
	return "", false
}

// EcosystemCache serves the schema from memory, then from disk, then from
// Thunderstore.
//
// The disk copy matters more than the memory one: a build must not fail
// because Thunderstore happens to be down, and an hours-old game definition is
// never the reason a modpack lays out wrong.
type EcosystemCache struct {
	client *Client
	path   string

	mu        sync.Mutex
	cached    *Ecosystem
	fetchedAt time.Time
}

// NewEcosystemCache stores its disk copy under the content root's tmp
// directory, next to the package archive cache.
func NewEcosystemCache(client *Client, contentRoot string) *EcosystemCache {
	return &EcosystemCache{
		client: client,
		path:   filepath.Join(contentRoot, "tmp", "ecosystem.json"),
	}
}

// Get returns the schema, refreshing it when the in-memory copy has expired.
//
// A failed refresh is not fatal while any copy exists: the stale schema is
// returned and the failure is logged. Only a cold start with no disk copy and
// no network can fail outright.
func (c *EcosystemCache) Get(ctx context.Context) (*Ecosystem, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.cached != nil && time.Since(c.fetchedAt) < ecosystemTTL {
		return c.cached, nil
	}

	if eco, at, ok := c.loadDisk(); ok && time.Since(at) < ecosystemTTL {
		c.cached, c.fetchedAt = eco, at
		return eco, nil
	}

	eco, err := c.fetch(ctx)
	if err != nil {
		if c.cached != nil {
			log.Printf("[mods] ecosystem refresh failed, serving in-memory copy: %v", err)
			return c.cached, nil
		}
		if stale, at, ok := c.loadDisk(); ok {
			log.Printf("[mods] ecosystem refresh failed, serving disk copy from %s: %v", at.Format(time.RFC3339), err)
			c.cached, c.fetchedAt = stale, at
			return stale, nil
		}
		return nil, err
	}

	c.cached, c.fetchedAt = eco, time.Now()
	c.storeDisk(eco)
	return eco, nil
}

// Game looks a game up by its schema key.
func (c *EcosystemCache) Game(ctx context.Context, label string) (EcoGame, error) {
	eco, err := c.Get(ctx)
	if err != nil {
		return EcoGame{}, err
	}
	g, ok := eco.Games[strings.ToLower(strings.TrimSpace(label))]
	if !ok {
		return EcoGame{}, fmt.Errorf("mods: game %q is not in the thunderstore ecosystem schema", label)
	}
	return g, nil
}

func (c *EcosystemCache) fetch(ctx context.Context) (*Ecosystem, error) {
	release, err := c.client.acquire(ctx)
	if err != nil {
		return nil, err
	}
	defer release()

	reqCtx, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, c.ecosystemURL(), nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", userAgent)

	res, err := c.client.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("mods: ecosystem schema: status %d", res.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(res.Body, maxEcosystemBytes))
	if err != nil {
		return nil, err
	}
	var eco Ecosystem
	if err := json.Unmarshal(body, &eco); err != nil {
		return nil, fmt.Errorf("mods: decode ecosystem schema: %w", err)
	}
	if len(eco.Games) == 0 {
		return nil, errors.New("mods: ecosystem schema has no games")
	}
	return &eco, nil
}

// ecosystemURL honours a client pointed at a test server.
func (c *EcosystemCache) ecosystemURL() string {
	if c.client.apiBase == DefaultAPIBase {
		return ecosystemURL
	}
	return c.client.apiBase + "/api/experimental/schema/dev/latest/"
}

func (c *EcosystemCache) loadDisk() (*Ecosystem, time.Time, bool) {
	st, err := os.Stat(c.path)
	if err != nil {
		return nil, time.Time{}, false
	}
	// #nosec G304 -- c.path is the content root plus two constant components.
	b, err := os.ReadFile(c.path)
	if err != nil {
		return nil, time.Time{}, false
	}
	var eco Ecosystem
	if err := json.Unmarshal(b, &eco); err != nil || len(eco.Games) == 0 {
		return nil, time.Time{}, false
	}
	return &eco, st.ModTime(), true
}

func (c *EcosystemCache) storeDisk(eco *Ecosystem) {
	b, err := json.Marshal(eco)
	if err != nil {
		return
	}
	if err := os.MkdirAll(filepath.Dir(c.path), 0o755); err != nil { // #nosec G301 -- tmp tree, same perms as the rest of contentRoot
		log.Printf("[mods] ecosystem cache dir: %v", err)
		return
	}
	if err := adminutil.WriteFileAtomic(c.path, b, 0o644); err != nil {
		log.Printf("[mods] store ecosystem cache: %v", err)
	}
}
