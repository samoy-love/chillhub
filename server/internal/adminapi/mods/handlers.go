package mods

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"ChillHub/server/internal/adminapi/builds"
	"ChillHub/server/internal/adminapi/games"
	"ChillHub/server/internal/adminutil"
)

// The admin endpoints.
//
// Everything a modpack needs from Thunderstore goes through this process, never
// through the operator's browser: the panel's fetch wrapper rewrites paths and
// attaches a CSRF token, and a cross-origin call from there would hit CORS and
// leak panel traffic to a third party at the same time.

const (
	// maxProfileBytes bounds an uploaded r2modman profile. The real mods.yml
	// from the Lethal Company build — 237 mods — is 190 KB.
	maxProfileBytes = 8 << 20

	// buildTimeout bounds one build. LethalReloaded is 151 packages and 1.8 GB;
	// an hour is generous on a slow link and still finite.
	buildTimeout = time.Hour

	// resolveTimeout bounds a resolve-only request.
	resolveTimeout = 15 * time.Minute

	// maxFormBytes bounds a plain form body. Every field these endpoints read
	// is a slug, a name or a version; nothing legitimate approaches this.
	maxFormBytes = 64 << 10
)

// Handlers serves the modpack endpoints.
type Handlers struct {
	builder *Builder
	games   *games.Handlers
	builds  *builds.Handlers

	// sum кеширует сводку «что ждёт действия» — см. summary.go.
	sum summaryCache
}

// New returns handlers for one content root.
func New(root string, b *builds.Handlers, g *games.Handlers) *Handlers {
	return &Handlers{builder: NewBuilder(root, b), games: g, builds: b}
}

// Builder exposes the pipeline, for the scheduled cache sweep in main.
func (h *Handlers) Builder() *Builder { return h.builder }

// gameConfig resolves a request's gameId into its registry row and mods
// configuration, answering the client itself on failure.
func (h *Handlers) gameConfig(w http.ResponseWriter, r *http.Request) (games.Entry, *games.ModsConfig, bool) {
	gid := strings.TrimSpace(r.FormValue("gameId"))
	if gid == "" {
		gid = strings.TrimSpace(r.URL.Query().Get("gameId"))
	}
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return games.Entry{}, nil, false
	}
	entry, ok := h.games.Entry(gid)
	if !ok {
		http.Error(w, "unknown gameId", http.StatusNotFound)
		return games.Entry{}, nil, false
	}
	if entry.Mods == nil || !entry.Mods.Enabled {
		http.Error(w, "у игры не включены моды", http.StatusBadRequest)
		return games.Entry{}, nil, false
	}
	return entry, entry.Mods, true
}

// Ecosystem fills a game's mods configuration from the Thunderstore ecosystem
// schema (POST gameId, slug).
//
// This is the "Подтянуть из Thunderstore" button. Doing it by hand means
// copying a Steam app id, an executable name and a folder name for every game,
// and getting the How to Fish case (a folder nested inside the install dir)
// wrong on the first try.
func (h *Handlers) Ecosystem(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	gid := strings.TrimSpace(r.FormValue("gameId"))
	slug := strings.ToLower(strings.TrimSpace(r.FormValue("slug")))
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	if !safeCommunity(slug) {
		http.Error(w, "invalid slug", http.StatusBadRequest)
		return
	}
	entry, ok := h.games.Entry(gid)
	if !ok {
		http.Error(w, "unknown gameId", http.StatusNotFound)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Minute)
	defer cancel()

	game, err := h.builder.Eco.Game(ctx, slug)
	if err != nil {
		adminutil.Fail(w, http.StatusBadGateway, err.Error(), "mods:ecosystem", err)
		return
	}
	def, hasDef := game.Def()
	if !hasDef {
		http.Error(w, "у этой игры нет правил установки модов в схеме Thunderstore", http.StatusBadRequest)
		return
	}

	cfg := entry.Mods
	if cfg == nil {
		cfg = &games.ModsConfig{}
	}
	cfg.Enabled = true
	cfg.Community = slug
	cfg.EcosystemGame = slug
	cfg.Loader = def.PackageLoader
	cfg.SteamAppID = game.SteamAppID()
	cfg.SteamFolder = def.SteamFolderName
	cfg.ExeNames = def.ExeNames
	if uuid, err := h.builder.Client.ModpacksSectionUUID(ctx, slug); err == nil {
		cfg.SectionUUID = uuid
	} else {
		log.Printf("[mods] section uuid for %s: %v", slug, err)
	}
	// Сохраняется только настройка модов. Запись реестра перечитывается внутри
	// SaveMods: между чтением выше и этой строкой прошли два обращения к
	// Thunderstore, а оператор в другой вкладке за это время мог переименовать
	// игру или снять её с публикации.
	if err := h.games.SaveMods(gid, cfg); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "не удалось сохранить реестр", "mods:ecosystem", err)
		return
	}
	adminutil.WriteJSON(w, map[string]any{
		"status":    "ok",
		"mods":      cfg,
		"browseUrl": h.builder.Client.BrowseURL(ctx, slug),
	})
}

// Catalog lists a game's modpacks on Thunderstore
// (GET gameId, q, ordering, page).
func (h *Handlers) Catalog(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodGet) {
		return
	}
	_, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	q := r.URL.Query()
	page, _ := strconv.Atoi(q.Get("page"))

	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Minute)
	defer cancel()

	section := cfg.SectionUUID
	if section == "" {
		if uuid, err := h.builder.Client.ModpacksSectionUUID(ctx, cfg.Community); err == nil {
			section = uuid
		}
	}
	res, err := h.builder.Client.Catalog(ctx, cfg.Community, section, q.Get("q"), q.Get("ordering"), page)
	if err != nil {
		adminutil.Fail(w, http.StatusBadGateway, "каталог Thunderstore недоступен", "mods:catalog", err)
		return
	}
	adminutil.WriteJSON(w, map[string]any{
		"count":     res.Count,
		"results":   res.Results,
		"browseUrl": h.builder.Client.BrowseURL(ctx, cfg.Community),
	})
}

// Readme returns a package's README (GET namespace, name, version).
func (h *Handlers) Readme(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodGet) {
		return
	}
	q := r.URL.Query()
	ns, name, version := q.Get("namespace"), q.Get("name"), q.Get("version")

	ctx, cancel := context.WithTimeout(r.Context(), time.Minute)
	defer cancel()

	if version == "" {
		p, err := h.builder.Client.GetPackage(ctx, ns, name)
		if err != nil {
			adminutil.Fail(w, http.StatusBadGateway, "пакет недоступен", "mods:readme", err)
			return
		}
		version = p.Latest.VersionNumber
	}
	md, err := h.builder.Client.GetReadme(ctx, ns, name, version)
	if err != nil {
		adminutil.Fail(w, http.StatusBadGateway, "README недоступен", "mods:readme", err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"markdown": md, "version": version})
}

// buildRequest turns a form into a Request, resolving "latest" when the
// operator picked a pack from the catalogue (which carries no version).
func (h *Handlers) buildRequest(ctx context.Context, r *http.Request, entry games.Entry, cfg *games.ModsConfig) (Request, error) {
	req := Request{
		GameID:        entry.GameID,
		EcosystemGame: cfg.EcosystemGame,
		Kind:          SourceThunderstore,
		Namespace:     strings.TrimSpace(r.FormValue("namespace")),
		Name:          strings.TrimSpace(r.FormValue("name")),
		Version:       strings.TrimSpace(r.FormValue("version")),
	}
	if req.EcosystemGame == "" {
		req.EcosystemGame = cfg.Community
	}

	// A pasted package link is accepted in place of the three fields: half the
	// modpacks on Thunderstore are not tagged into the Modpacks section and
	// cannot be found in the catalogue at all.
	if link := strings.TrimSpace(r.FormValue("packageUrl")); link != "" {
		_, ns, name, ok := ParsePackageURL(link)
		if !ok {
			return req, errors.New("ссылка не похожа на страницу пакета Thunderstore")
		}
		req.Namespace, req.Name = ns, name
	}
	if req.Namespace == "" || req.Name == "" {
		return req, errors.New("не указан модпак")
	}

	if req.Version == "" {
		p, err := h.builder.Client.GetPackage(ctx, req.Namespace, req.Name)
		if err != nil {
			return req, err
		}
		req.Version = p.Latest.VersionNumber
	}
	return req, nil
}

// Resolve previews a modpack without downloading it (POST).
func (h *Handlers) Resolve(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	entry, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), resolveTimeout)
	defer cancel()

	req, err := h.buildRequest(ctx, r, entry, cfg)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	plan, err := h.builder.Resolve(ctx, req)
	if err != nil {
		adminutil.Fail(w, http.StatusBadGateway, err.Error(), "mods:resolve", err)
		return
	}
	adminutil.WriteJSON(w, map[string]any{
		"version":      plan.Version,
		"displayName":  plan.DisplayName,
		"packages":     len(plan.Packages),
		"missing":      plan.Missing,
		"loader":       plan.Loader,
		"foreign":      plan.Foreign,
		"extraLoaders": plan.ExtraLoaders,
		"totalBytes":   plan.TotalBytes,
		"cachedBytes":  plan.CachedBytes,
		"spaceOk":      plan.SpaceOK,
		"spaceNote":    plan.SpaceNote,
		"packageUrl":   req.PackageURL(cfg.Community),
	})
}

// Build assembles and publishes a modpack version, streaming progress as
// NDJSON (POST).
func (h *Handlers) Build(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	entry, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), buildTimeout)
	defer cancel()

	req, err := h.buildRequest(ctx, r, entry, cfg)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.runBuild(ctx, w, req, cfg, truthy(r.FormValue("allowMissing")))
}

// RebuildRequest reconstructs the build behind an already published version.
//
// ПОЧЕМУ ЭТО ЧИТАЕТСЯ ИЗ ЗАПИСИ, А НЕ СОБИРАЕТСЯ ЗАНОВО ИЗ ИМЕНИ ВЕРСИИ.
//
// Имя версии модпака с Thunderstore — это «пространство-имя-версия», из него
// исходную посылку восстановить можно. У импортированного профиля имя своё,
// какое назвал оператор, и что в него входило, знает только запись рядом с
// манифестом. Собрать такую версию «по имени» нельзя вовсе, а собрать не то и
// опубликовать под тем же номером — хуже, чем отказаться.
func (h *Handlers) rebuildRequest(entry games.Entry, cfg *games.ModsConfig, version string) (Request, error) {
	src, err := h.builder.ReadSource(entry.GameID, version)
	if err != nil {
		return Request{}, fmt.Errorf("нет записи о сборке версии %s: %w", version, err)
	}

	req := Request{
		GameID:        entry.GameID,
		EcosystemGame: orDefault(cfg.EcosystemGame, cfg.Community),
		Kind:          src.Kind,
		Roots:         src.Roots,
	}
	switch src.Kind {
	case SourceThunderstore:
		ns, name, ver, ok := SplitDependency(version)
		if !ok {
			return Request{}, fmt.Errorf("из имени версии %q не выделить пакет", version)
		}
		req.Namespace, req.Name, req.Version = ns, name, ver
		// Записи, сделанные до появления Roots, всё равно пересобираются: у
		// модпака с Thunderstore посылка — он сам, и она в имени версии.
		if len(req.Roots) == 0 {
			req.Roots = []string{version}
		}
	case SourceProfile:
		req.ProfileVersion = version
		if len(req.Roots) == 0 {
			return Request{}, fmt.Errorf(
				"версия %s собрана из профиля r2modman до того, как состав стал записываться, "+
					"и восстановить его нечем — загрузите профиль заново через «Импорт»", version)
		}
	default:
		return Request{}, fmt.Errorf("неизвестный источник версии %s: %q", version, src.Kind)
	}
	return req, nil
}

// Rebuild assembles an already published version again, streaming progress as
// NDJSON (POST gameId, version).
//
// Тот же состав, сегодняшние правила раскладки. Нужно это ровно тогда, когда
// правила изменились: сборка, разложенная старым конвейером, останется лежать
// как есть, пока её не тронешь, — а понять по панели, что она устарела не
// версией, а способом сборки, нельзя никак.
//
// Версия публикуется под тем же именем и поверх себя. latest.json при этом не
// трогается: если версия была активной, игроки получат новое дерево, если не
// была — она так и останется ждать активации.
func (h *Handlers) Rebuild(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	entry, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	version := strings.TrimSpace(r.FormValue("version"))
	if !adminutil.IsSafeVersion(version) {
		http.Error(w, "нужно имя уже собранной версии", http.StatusBadRequest)
		return
	}

	req, err := h.rebuildRequest(entry, cfg, version)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	log.Printf("[mods] rebuild %s/%s: источник %q, корней %d",
		entry.GameID, version, req.Kind, len(req.Roots))

	ctx, cancel := context.WithTimeout(r.Context(), buildTimeout)
	defer cancel()
	h.runBuild(ctx, w, req, cfg, truthy(r.FormValue("allowMissing")))
}

// Import builds a modpack from an uploaded r2modman profile (POST multipart).
//
// This is the migration path off the current builds: their mods.yml names
// every installed mod and its exact version, so the set players already have
// can be republished as a modpack instead of being approximated.
func (h *Handlers) Import(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxProfileBytes+(1<<20))
	if err := r.ParseMultipartForm(maxProfileBytes); err != nil {
		http.Error(w, "request too large or malformed", http.StatusBadRequest)
		return
	}
	entry, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}

	file, _, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file", http.StatusBadRequest)
		return
	}
	defer func() { _ = file.Close() }()
	content, err := io.ReadAll(io.LimitReader(file, maxProfileBytes+1))
	if err != nil || len(content) > maxProfileBytes {
		http.Error(w, "profile too large", http.StatusRequestEntityTooLarge)
		return
	}

	version := strings.TrimSpace(r.FormValue("version"))
	if !adminutil.IsSafeVersion(version) {
		http.Error(w, "нужно имя версии из букв, цифр, дефиса, подчёркивания и точки", http.StatusBadRequest)
		return
	}

	// Parsed here, before anything long-running starts, so a file that is not a
	// profile fails as a plain 400 instead of a stream that dies on its first
	// event.
	list, err := ParseProfile(string(content))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	log.Printf("[mods] import %s: %d mods in profile, %d enabled", entry.GameID, len(list), len(EnabledDependencies(list)))

	req := Request{
		GameID:         entry.GameID,
		EcosystemGame:  orDefault(cfg.EcosystemGame, cfg.Community),
		Kind:           SourceProfile,
		ProfileContent: string(content),
		ProfileVersion: version,
	}
	ctx, cancel := context.WithTimeout(r.Context(), buildTimeout)
	defer cancel()
	h.runBuild(ctx, w, req, cfg, truthy(r.FormValue("allowMissing")))
}

// runBuild streams one build as NDJSON.
//
// The stream is the only honest way to report this: a build downloads up to
// 1.8 GB across a hundred and fifty requests, and a request that simply holds
// the connection open for twenty minutes looks identical to a hung one.
func (h *Handlers) runBuild(ctx context.Context, w http.ResponseWriter, req Request, cfg *games.ModsConfig, allowMissing bool) {
	w.Header().Set("Content-Type", "application/x-ndjson")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)

	fl := adminutil.FlusherFor(w)
	enc := json.NewEncoder(w)
	emit := func(ev Event) {
		if err := enc.Encode(ev); err != nil {
			return
		}
		fl.Flush()
	}

	src, err := h.builder.Build(ctx, req, allowMissing, emit)
	if err != nil {
		log.Printf("[mods] build %s/%s failed: %v", req.GameID, req.VersionName(), err)
		emit(Event{Type: "error", Message: err.Error()})
		return
	}

	// The pack URL is stored after the fact so the panel can link a built
	// version back to its page without recomputing it from the version name.
	src.PackageURL = req.PackageURL(cfg.Community)
	if err := h.builder.writeSource(req.GameID, src.Version, src); err != nil {
		log.Printf("[mods] update source record %s/%s: %v", req.GameID, src.Version, err)
	}
}

// VersionInfo is one published modpack version as the panel shows it.
type VersionInfo struct {
	Version     string `json:"version"`
	DisplayName string `json:"displayName,omitempty"`
	PackageURL  string `json:"packageUrl,omitempty"`
	Kind        string `json:"kind,omitempty"`
	Active      bool   `json:"active"`
	CreatedAt   string `json:"createdAt,omitempty"`
	Files       int    `json:"files"`
	Bytes       int64  `json:"bytes"`
	Packages    int    `json:"packages"`
	Missing     int    `json:"missing"`

	// Rebuildable is false for a version whose build cannot be reconstructed —
	// an r2modman import recorded before the composition was kept. The panel
	// disables the button instead of offering a request that can only fail.
	Rebuildable bool `json:"rebuildable"`

	// Collisions is how many places two of this version's packages met.
	Collisions int `json:"collisions,omitempty"`
}

// List returns a game's built modpack versions plus the update check
// (GET gameId).
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodGet) {
		return
	}
	entry, cfg, ok := h.gameConfig(w, r)
	if !ok {
		return
	}

	versions, err := h.builds.ListPublished(builds.NamespaceMods, entry.GameID)
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "не удалось прочитать версии", "mods:list", err)
		return
	}
	active := h.builds.LatestVersion(builds.NamespaceMods, entry.GameID)

	items := make([]VersionInfo, 0, len(versions))
	for _, v := range versions {
		createdAt, files, bytesTotal := h.builds.VersionStats(builds.NamespaceMods, entry.GameID, v)
		info := VersionInfo{
			Version: v, Active: v == active,
			CreatedAt: createdAt, Files: files, Bytes: bytesTotal,
		}
		if src, err := h.builder.ReadSource(entry.GameID, v); err == nil {
			info.DisplayName = src.DisplayName
			info.PackageURL = src.PackageURL
			info.Kind = string(src.Kind)
			info.Packages = len(src.Tree)
			info.Missing = len(src.Missing)
			info.Collisions = len(src.Collisions)
			info.Rebuildable = src.Kind == SourceThunderstore || len(src.Roots) > 0
		}
		items = append(items, info)
	}
	// Newest first: the operator is almost always looking at what was just
	// built, and version names of different packs do not order meaningfully
	// against each other anyway.
	sort.SliceStable(items, func(i, j int) bool { return items[i].CreatedAt > items[j].CreatedAt })

	out := map[string]any{
		"gameId": entry.GameID, "items": items, "active": active,
		"community": cfg.Community,
	}

	// Update check: one request per distinct pack, cheap enough to do on every
	// panel visit.
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Minute)
	defer cancel()
	out["updates"] = h.updateChecks(ctx, entry.GameID, items)

	adminutil.WriteJSON(w, out)
}

// updateCheck reports a newer version available on Thunderstore.
type updateCheck struct {
	Version    string `json:"version"`
	Namespace  string `json:"namespace"`
	Name       string `json:"name"`
	Latest     string `json:"latest"`
	Deprecated bool   `json:"deprecated"`
}

func (h *Handlers) updateChecks(ctx context.Context, gid string, items []VersionInfo) []updateCheck {
	seen := map[string]bool{}
	var out []updateCheck
	for _, it := range items {
		if ctx.Err() != nil {
			break
		}
		src, err := h.builder.ReadSource(gid, it.Version)
		if err != nil || src.Kind != SourceThunderstore {
			continue
		}
		ns, name, version, ok := SplitDependency(it.Version)
		if !ok || seen[PackageKey(ns, name)] {
			continue
		}
		seen[PackageKey(ns, name)] = true

		p, err := h.builder.Client.GetPackage(ctx, ns, name)
		if err != nil {
			log.Printf("[mods] update check %s-%s: %v", ns, name, err)
			continue
		}
		if p.Latest.VersionNumber != version || p.IsDeprecated {
			out = append(out, updateCheck{
				Version: it.Version, Namespace: ns, Name: name,
				Latest: p.Latest.VersionNumber, Deprecated: p.IsDeprecated,
			})
		}
	}
	return out
}

// Activate makes a built version the one launchers receive (POST).
func (h *Handlers) Activate(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	entry, _, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	version := strings.TrimSpace(r.FormValue("version"))
	if err := h.builds.ActivateVersion(builds.NamespaceMods, entry.GameID, version); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	log.Printf("[mods] activated %s -> %s", entry.GameID, version)
	adminutil.WriteJSON(w, map[string]string{"status": "ok", "active": version})
}

// DeleteVersion removes a built version (POST).
func (h *Handlers) DeleteVersion(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	entry, _, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	version := strings.TrimSpace(r.FormValue("version"))
	if err := h.builder.DeleteVersion(entry.GameID, version); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Diff compares the contents of two built versions (GET gameId, from, to).
func (h *Handlers) Diff(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodGet) {
		return
	}
	entry, _, ok := h.gameConfig(w, r)
	if !ok {
		return
	}
	q := r.URL.Query()
	diff, err := h.builder.Diff(entry.GameID, q.Get("from"), q.Get("to"))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	sort.SliceStable(diff, func(i, j int) bool {
		if diff[i].Change != diff[j].Change {
			return diff[i].Change < diff[j].Change
		}
		return diff[i].Package < diff[j].Package
	})
	adminutil.WriteJSON(w, map[string]any{"items": diff})
}

// Cache reports the archive cache and, on POST, sweeps or clears it.
func (h *Handlers) Cache(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		files, bytesHeld := h.builder.Cache.Stats()
		adminutil.WriteJSON(w, map[string]any{
			"files": files, "bytes": bytesHeld, "ttlDays": int(CacheTTL.Hours() / 24),
		})
	case http.MethodPost:
		r.Body = http.MaxBytesReader(w, r.Body, maxFormBytes)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "malformed form", http.StatusBadRequest)
			return
		}
		var removed int
		var freed int64
		if truthy(r.FormValue("all")) {
			removed, freed = h.builder.Cache.Clear()
		} else {
			removed, freed = h.builder.Cache.Sweep()
		}
		adminutil.WriteJSON(w, map[string]any{"status": "ok", "removed": removed, "freed": freed})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func truthy(s string) bool {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "1", "true", "yes", "on":
		return true
	}
	return false
}

func orDefault(v, fallback string) string {
	if strings.TrimSpace(v) == "" {
		return fallback
	}
	return v
}
