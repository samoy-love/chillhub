// Package games serves the admin-managed game registry
// (manifests/_registry/games.json) and the per-game icon upload.
package games

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"

	"ChillHub/server/internal/adminapi/media"
	"ChillHub/server/internal/adminutil"
)

// Entry is one record of the games registry.
type Entry struct {
	GameID          string `json:"gameId"`
	Title           string `json:"title"`
	ExeRelativePath string `json:"exeRelativePath"`
	IconURL         string `json:"iconUrl"`
	Order           int    `json:"order"`
	Pinned          bool   `json:"pinned"`
	// Unpublished hides the game from the public /api/games the launcher reads,
	// while keeping its registry row, manifests and builds on disk.
	//
	// The negative spelling is deliberate: the zero value has to mean "visible",
	// or every registry written before this field existed — and every entry a
	// scan adds — would silently vanish from the launcher the moment the server
	// was updated.
	Unpublished bool `json:"unpublished,omitempty"`

	// Mods holds the Thunderstore configuration of a game that has a modpack.
	//
	// A POINTER with omitempty, so every registry written before this field
	// existed round-trips byte for byte: the panel saves the whole registry on
	// every edit, and a nil here must not start appending an empty object to
	// three hundred entries that have nothing to do with mods.
	Mods *ModsConfig `json:"mods,omitempty"`
}

// ModsConfig describes where a game's mods come from and where the game itself
// is installed.
//
// Most of it is not typed in by hand: the panel fills it from the Thunderstore
// ecosystem schema, which publishes the Steam app id, the executable names and
// the install folder for 326 games. Only Enabled and Community are decisions;
// the rest is copied, with the fields left editable because the schema is
// occasionally wrong for a specific build.
type ModsConfig struct {
	// Enabled turns the whole feature on for this game.
	Enabled bool `json:"enabled"`

	// Community is the Thunderstore community slug ("lethal-company").
	Community string `json:"community,omitempty"`

	// EcosystemGame is the game's key in the ecosystem schema. Usually equal to
	// Community, but they are separate keys and do diverge.
	EcosystemGame string `json:"ecosystemGame,omitempty"`

	// SectionUUID is the id of the community's "Modpacks" section. Cached here
	// because it differs per game and the catalogue filter silently does
	// nothing when addressed by slug instead.
	SectionUUID string `json:"sectionUuid,omitempty"`

	// Loader is the mod loader ("bepinex").
	Loader string `json:"loader,omitempty"`

	// SteamAppID identifies the game to Steam, for locating and launching the
	// player's own copy.
	SteamAppID string `json:"steamAppId,omitempty"`

	// SteamFolder is the folder under steamapps/common. It is not always the
	// same as the Steam install dir: How to Fish nests one level deeper
	// ("How to Fish/How to Fish"), and a launcher that assumes otherwise lands
	// in a directory with no executable in it.
	SteamFolder string `json:"steamFolder,omitempty"`

	// ExeNames are the game's executables, in preference order.
	ExeNames []string `json:"exeNames,omitempty"`
}

// reservedGameIDs are directory names under manifests/ that are not games.
var reservedGameIDs = map[string]bool{
	"_registry": true,
	"_mods":     true,
}

// Handlers serves the games endpoints for one content root.
type Handlers struct {
	root string

	// mu сериализует «прочитал реестр — изменил — записал».
	//
	// Писателей у games.json четыре — Save, SaveMods, dropRegistryEntry и
	// первичная генерация, — и ни один не был отделён от остальных, в отличие
	// от news, feedback и gamegallery, где такой мьютекс заведён именно под
	// этот цикл. Запись атомарна, но проигравший всё равно кладёт на диск
	// версию, прочитанную ДО чужой правки: «Подтянуть из Thunderstore» уходит
	// в сеть на две минуты, и сохранение, случившееся за это время, откатом
	// возвращалось назад — снятая с публикации игра снова становилась видна
	// игрокам, а ответ был «ok».
	mu sync.Mutex
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// registryPath stores the registry separately from any game ID to avoid collisions.
func (h *Handlers) registryPath() string {
	return filepath.Join(h.root, "manifests", "_registry", "games.json")
}

// FromManifests scans manifests/* and builds an initial registry list.
func (h *Handlers) FromManifests() []Entry {
	base := filepath.Join(h.root, "manifests")
	entries, err := os.ReadDir(base)
	if err != nil {
		return []Entry{}
	}
	items := make([]Entry, 0)
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		gid := e.Name()
		name := strings.ToLower(gid)
		// Skip special/system folders that are not games
		if name == "repo" || name == "launcher" || reservedGameIDs[name] {
			continue
		}
		items = append(items, Entry{GameID: gid, Title: gid, ExeRelativePath: "", IconURL: ""})
	}
	sort.Slice(items, func(i, j int) bool { return items[i].GameID < items[j].GameID })
	return items
}

// SortEntries orders the registry the way the launcher's gallery expects it:
// pinned games first, then by the operator-assigned Order, then by GameID so
// that entries tied on both stay in a stable, predictable order instead of
// whatever order the JSON file happened to list them in.
//
// Exported so server/cmd/api (a separate binary that also reads games.json,
// for the public /api/games the launcher actually calls) can reuse this exact
// comparator instead of carrying its own copy — two independent
// implementations of "pinned/order/id" is exactly how the registry's ordering
// silently drifted between what the admin panel showed and what players saw.
func SortEntries(items []Entry) {
	sortEntries(items)
}

func sortEntries(items []Entry) {
	sort.Slice(items, func(i, j int) bool {
		a, b := items[i], items[j]
		if a.Pinned != b.Pinned {
			return a.Pinned && !b.Pinned
		}
		if a.Order != b.Order {
			return a.Order < b.Order
		}
		return a.GameID < b.GameID
	})
}

// decodeRegistryItems parses a `{"items": [...]}` payload (from disk or from a
// Save() request body) into entries, defaulting Order to each item's original
// position for any item whose raw JSON has no "order" key at all — Order's zero
// value is otherwise indistinguishable from "explicitly pinned at position 0",
// which used to collapse every legacy entry to alphabetical-by-GameID.
//
// ok is false whenever the payload isn't recognizable as `{"items": [...]}` at
// all (parse failure, or the "items" key missing entirely — including a bare
// top-level array, which unmarshals into a zero-value wrapper without error and
// would otherwise silently become an empty registry).
//
// strict controls what happens to an item that IS present but doesn't unmarshal
// into Entry: Get() (reading already-stored data) passes false and skips+logs
// the one bad item rather than failing the whole response; Save() (accepting a
// fresh write) passes true and rejects the whole request, matching how it
// already rejects an unsafe GameID two lines below — dropping a bad entry on
// write is the same silent-cure anti-pattern that check exists to prevent.
func decodeRegistryItems(b []byte, strict bool) (items []Entry, ok bool) {
	var top map[string]json.RawMessage
	if err := json.Unmarshal(b, &top); err != nil {
		return nil, false
	}
	itemsRaw, hasItems := top["items"]
	if !hasItems {
		return nil, false
	}
	var rawItems []json.RawMessage
	if err := json.Unmarshal(itemsRaw, &rawItems); err != nil {
		return nil, false
	}
	items = make([]Entry, 0, len(rawItems))
	for i, item := range rawItems {
		var e Entry
		if err := json.Unmarshal(item, &e); err != nil {
			if strict {
				return nil, false
			}
			log.Printf("[games] skipping malformed registry entry %d: %v", i, err)
			continue
		}
		var probe map[string]json.RawMessage
		if err := json.Unmarshal(item, &probe); err == nil {
			if _, hasOrder := probe["order"]; !hasOrder {
				e.Order = i
			}
		}
		items = append(items, e)
	}
	return items, true
}

// Get returns the registry, autogenerating it from the manifests on first use.
func (h *Handlers) Get(w http.ResponseWriter, _ *http.Request) {
	p := h.registryPath()
	if _, err := os.Stat(p); err != nil {
		h.serveAutogenerated(w, p)
		return
	}
	// #nosec G304 -- p is registryPath(): the content root plus three constant
	// path components. No part of it comes from the request.
	b, err := os.ReadFile(p)
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to read the registry", "games", err)
		return
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		// The stored registry isn't recognizable {"items":[...]} JSON at all;
		// hand it back as-is rather than failing the request outright, matching
		// the previous behaviour.
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(b)
		return
	}
	sortEntries(items)
	adminutil.WriteJSON(w, struct {
		Items []Entry `json:"items"`
	}{Items: items})
}

// All returns every registry row.
//
// Exported for the same reason as Entry: the summary endpoint needs to know
// which games have modpacks, and a second parser for games.json would be a
// second place where "order" defaulting and unsafe-id dropping could drift.
func (h *Handlers) All() ([]Entry, error) {
	// #nosec G304 -- registryPath() is the content root plus three constants.
	b, err := os.ReadFile(h.registryPath())
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		return nil, errors.New("games: registry is not readable as {\"items\":[...]}")
	}
	sortEntries(items)
	return items, nil
}

// Entry returns one registry row by game id.
//
// Exported so the modpack endpoints can read a game's ModsConfig without a
// second parser for games.json: the registry has exactly one reader per
// process today, and that is the only reason its "order" defaulting and its
// unsafe-id dropping behave identically everywhere.
func (h *Handlers) Entry(gid string) (Entry, bool) {
	// #nosec G304 -- registryPath() is the content root plus three constants.
	b, err := os.ReadFile(h.registryPath())
	if err != nil {
		return Entry{}, false
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		return Entry{}, false
	}
	for _, it := range items {
		if strings.EqualFold(it.GameID, gid) {
			return it, true
		}
	}
	return Entry{}, false
}

// SaveMods stores one game's modpack configuration, leaving every other field
// of that row — and every other row — exactly as it is on disk.
//
// ЗАПИСЫВАЕТСЯ ОДНО ПОЛЕ, А НЕ СТРОКА ЦЕЛИКОМ. Раньше вызывающий читал запись,
// уходил в Thunderstore на две минуты и клал прочитанное обратно вместе с
// настройкой модов. Всё, что оператор успевал изменить за это время в другой
// вкладке — название, порядок, закрепление, снятие с публикации, — молча
// откатывалось на две минуты назад, и ответ был «ok». Реестр перечитывается
// здесь, под замком, непосредственно перед записью.
func (h *Handlers) SaveMods(gid string, cfg *ModsConfig) error {
	if !adminutil.IsSafeGameID(gid) || reservedGameIDs[strings.ToLower(gid)] {
		return fmt.Errorf("games: unusable gameId %q", gid)
	}
	h.mu.Lock()
	defer h.mu.Unlock()

	// #nosec G304 -- see Entry.
	b, err := os.ReadFile(h.registryPath())
	if err != nil {
		return err
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		return errors.New("games: registry is not readable as {\"items\":[...]}")
	}
	found := false
	for i := range items {
		if strings.EqualFold(items[i].GameID, gid) {
			items[i].Mods = cfg
			found = true
			break
		}
	}
	if !found {
		return fmt.Errorf("games: %q is not in the registry", gid)
	}
	return h.storeRegistry(items)
}

// storeRegistry sorts and writes the registry. The caller must hold h.mu.
func (h *Handlers) storeRegistry(items []Entry) error {
	sortEntries(items)
	out, err := json.MarshalIndent(struct {
		Items []Entry `json:"items"`
	}{Items: items}, "", "  ")
	if err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(h.registryPath(), out, 0o644)
}

// Purge deletes a game outright: its registry row, its manifests and every
// extracted build (POST gameId).
//
// It exists because the panel's «Удалить игру и все версии» button only ever
// removed the registry row — the manifests and the unpacked builds stayed on
// disk, invisible to the panel and counted by nobody, while the button's own
// label promised the versions were gone too.
//
// Deleting the files is irreversible, so the id is validated the same way every
// path-forming id is, and both trees are confirmed to stay inside the content
// root before anything is removed.
func (h *Handlers) Purge(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	gid := strings.TrimSpace(r.FormValue("gameId"))
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	// Reserved ids, refused here exactly as Save refuses them. They are not
	// games but directory names under manifests/: "_registry" holds games.json,
	// the registry of every game, and "_mods" holds every modpack ever built.
	// IsSafeGameID accepts both — an underscore is a legal character — and both
	// stay comfortably inside their roots, so nothing further down said no: the
	// endpoint deleted the registry, answered "ok", and the launcher got an
	// empty list of games.
	if reservedGameIDs[strings.ToLower(gid)] {
		http.Error(w, "gameId is reserved for internal use", http.StatusBadRequest)
		return
	}
	manifests := filepath.Join(h.root, "manifests")
	content := filepath.Join(h.root, "content")
	manDir := filepath.Join(manifests, gid)
	conDir := filepath.Join(content, gid)
	if !adminutil.EnsureStrictlyWithin(manifests, manDir) || !adminutil.EnsureStrictlyWithin(content, conDir) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	// The registry row goes first. If removing the trees then fails halfway, the
	// game is already invisible to players and to the panel, and what is left is
	// a leftover directory for the operator — the other order would leave a
	// listed game whose files are gone.
	if err := h.dropRegistryEntry(gid); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the registry", "games", err)
		return
	}
	// ЧТО НЕ УДАЛИЛОСЬ, НАЗЫВАЕТСЯ В ОТВЕТЕ. Обе ошибки только писались в
	// журнал, ответ был «ok», а панель печатала «удалена вместе с манифестами и
	// сборками». Строки в реестре к этому моменту уже нет, так что застрявшее
	// дерево — заблокированный файл, точка монтирования, потерянное право —
	// остаётся на диске, которого не видно ниоткуда: ни в панели, ни в списке
	// игр, зато в показателе свободного места.
	deleted := []string{}
	failed := []string{}
	for _, part := range []struct {
		name string
		dir  string
	}{{"manifests", manDir}, {"content", conDir}} {
		// #nosec G703 -- gid passed IsSafeGameID, is not a reserved id, and both
		// paths were confirmed to stay strictly inside their roots above.
		if err := os.RemoveAll(part.dir); err != nil {
			log.Printf("[games] purge %s %s: %v", part.name, gid, err)
			failed = append(failed, part.name)
			continue
		}
		deleted = append(deleted, part.name)
	}
	if len(failed) > 0 {
		// Отказом, а не полем в теле: панель печатает свою фразу «удалена
		// вместе с манифестами и сборками» на любой успешный ответ и тело не
		// читает. Статус — единственное, что до оператора точно дойдёт.
		http.Error(w,
			fmt.Sprintf("the registry row is gone, but these trees are still on disk: %s", strings.Join(failed, ", ")),
			http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{
		"status":  "ok",
		"deleted": deleted,
		"failed":  failed,
	})
}

// dropRegistryEntry removes gid from the stored registry, leaving the file
// canonically ordered like every other writer does. A registry that does not
// exist yet, or does not list gid, is not an error: the caller's goal is that
// the game is absent, and it already is.
func (h *Handlers) dropRegistryEntry(gid string) error {
	h.mu.Lock()
	defer h.mu.Unlock()

	p := h.registryPath()
	// #nosec G304 -- p is registryPath(): the content root plus three constant
	// path components. No part of it comes from the request.
	b, err := os.ReadFile(p)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		return nil
	}
	kept := items[:0]
	for _, it := range items {
		if !strings.EqualFold(it.GameID, gid) {
			kept = append(kept, it)
		}
	}
	sortEntries(kept)
	out, err := json.MarshalIndent(struct {
		Items []Entry `json:"items"`
	}{Items: kept}, "", "  ")
	if err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(p, out, 0o644)
}

// serveAutogenerated builds the registry from manifests/{gameId}/ on first use
// and stores it: on a fresh server the panel must still list the games that
// have manifests, or it looks empty on a server that is serving games.
//
// Failing to persist is logged but not fatal — the answer is still correct and
// the next request simply regenerates it.
//
// The write goes under the same mutex as every other writer of games.json, and
// the file is re-checked under it. This one is a writer too, and it was the one
// left out: a GET that arrives on a server with no registry yet used to race a
// concurrent Save or Ecosystem, and the scan — which knows only ids, no titles,
// no exe paths, no mods config — could land on top of a row somebody had just
// filled in. Both writes are atomic, so nothing is corrupt; the edit is simply
// gone, and both requests answer success.
func (h *Handlers) serveAutogenerated(w http.ResponseWriter, p string) {
	h.mu.Lock()
	defer h.mu.Unlock()

	// Somebody may have written a real registry between the caller's stat and
	// this lock. Theirs wins: it has the data a manifest scan cannot recover.
	// #nosec G304 -- p is registryPath(): the content root plus three constant
	// path components. No part of it comes from the request.
	if stored, err := os.ReadFile(p); err == nil {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(stored)
		return
	}

	b, err := json.MarshalIndent(struct {
		Items []Entry `json:"items"`
	}{Items: h.FromManifests()}, "", "  ")
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to build the registry", "games", err)
		return
	}
	// 0o755/0o644 here and below: manifests/ is the tree nginx serves straight
	// from disk, so it has to stay readable outside this process.
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil { // #nosec G301 -- see above
		log.Printf("[games] create registry dir: %v", err)
	} else if err := adminutil.WriteFileAtomic(p, b, 0o644); err != nil {
		log.Printf("[games] store autogenerated registry: %v", err)
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write(b)
}

// maxRegistryBytes bounds the posted registry.
//
// Nothing outside this process limits it: the admin http.Server runs with
// ReadTimeout and WriteTimeout deliberately at zero, and nginx allows 30 GB on
// the admin routes for the build uploads. io.ReadAll below therefore buffered
// whatever it was sent until the process died — taking the public
// /feedback/submit and /metrics/report down with it, because the same process
// serves them. Three hundred games with a full mods config are well under a
// megabyte; four is room for a decade of growth.
const maxRegistryBytes = 4 << 20

// Save overwrites the registry with the posted list.
func (h *Handlers) Save(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxRegistryBytes))
	if err != nil {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	// strict=true: any single entry that doesn't parse rejects the whole save
	// (see decodeRegistryItems doc) — same reasoning as the GameID check below,
	// just applied one step earlier. This also gives entries missing "order"
	// (e.g. freshly scanned-in games) the same position-based default Get()
	// computes, instead of a literal order:0 that Save() used to bake onto
	// disk permanently — Get()'s own "missing key" detection can only work
	// for as long as the key is actually still missing on disk.
	items, ok := decodeRegistryItems(body, true)
	if !ok {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	// Reject a bad id HERE, not later on read.
	//
	// Every id in this file becomes a path component: the public API joins it
	// onto the manifests directory. The reader drops entries that do not pass
	// this check, but dropping is a silent cure — a typo in the id made the game
	// vanish from the launcher while the panel still reported "saved". Refusing
	// the save is the honest answer: the operator learns immediately which entry
	// is wrong instead of hunting for a game that no longer appears.
	seen := make(map[string]int, len(items))
	for i, it := range items {
		// Reserved ids. These are directory names under manifests/ that hold
		// something other than a game: the registry itself and the modpack
		// subtree. IsSafeGameID accepts both (an underscore is a legal
		// character), so a game saved under one of them would publish its
		// builds straight over that tree.
		if reservedGameIDs[strings.ToLower(strings.TrimSpace(it.GameID))] {
			http.Error(w,
				fmt.Sprintf("entry %d: gameId %q is reserved for internal use", i+1, it.GameID),
				http.StatusBadRequest)
			return
		}
		if !adminutil.IsSafeGameID(it.GameID) {
			http.Error(w,
				fmt.Sprintf("entry %d: gameId must be non-empty and contain only letters, digits, '-' or '_'", i+1),
				http.StatusBadRequest)
			return
		}
		// Two rows for the same id (e.g. after merging a manual add with a
		// "Найти новые" scan) is the same silent-cure risk as an unsafe id: the
		// launcher and every gameId-keyed lookup (icon upload, gallery, latest
		// version) would have to pick one arbitrarily, with no error telling
		// the operator two rows are now fighting over the same game.
		if first, dup := seen[it.GameID]; dup {
			http.Error(w,
				fmt.Sprintf("entry %d: gameId %q duplicates entry %d", i+1, it.GameID, first+1),
				http.StatusBadRequest)
			return
		}
		seen[it.GameID] = i
	}
	// Sorted HERE, once, at write time — not left for every reader to
	// re-derive independently. Get() used to be the only place that computed
	// Pinned/Order/GameID order, then server/cmd/api needed the exact same
	// comparator for the public API real players hit, and duplicating it there
	// was exactly how "pin looks like it works in the panel" and "pin actually
	// reaches players" could drift apart again. With the file itself always
	// canonically ordered, every reader can just trust file order.
	// ЧЕГО НЕТ В ПОСЫЛКЕ, ТО НЕ СТИРАЕТСЯ.
	//
	// Панель шлёт строку игры из шести полей — id, название, иконка, путь к
	// exe, порядок, закрепление, — а запись в реестре шире: там же лежит
	// настройка модов, которую пишут совсем другие обработчики. Полная замена
	// реестра присланным стирала её у ВСЕХ игр при каждом «Сохранить», включая
	// перетаскивание строки мышью. Со стороны оператора это выглядело как
	// «Моды для этой игры не настроены» назавтра после того, как он их
	// настроил.
	//
	// Чинится на сервере, а не в панели, намеренно: тогда никакой клиент — ни
	// нынешний, ни будущий, ни curl из консоли — не сможет снести поле, о
	// котором не знает.
	//
	// Чтение сохранённого и запись — один неделимый шаг: между ними успевает
	// пройти чужой SaveMods, и тогда только что записанная настройка модов
	// сливается из версии, прочитанной до неё.
	h.mu.Lock()
	defer h.mu.Unlock()
	items = mergeWithStored(items, body, h.storedByID())

	sortEntries(items)
	b, err := json.MarshalIndent(struct {
		Items []Entry `json:"items"`
	}{Items: items}, "", "  ")
	if err != nil {
		adminutil.Fail(w, http.StatusBadRequest, "failed to encode the registry", "games", err)
		return
	}
	outDir := filepath.Dir(h.registryPath())
	// 0o755/0o644: manifests/ is served straight from disk by nginx.
	if err := os.MkdirAll(outDir, 0o755); err != nil { // #nosec G301 -- see above
		adminutil.Fail(w, http.StatusInternalServerError, "failed to store the registry", "games", err)
		return
	}
	// The launcher reads this registry through the public API; a truncated write
	// would be served as-is.
	if err := adminutil.WriteFileAtomic(h.registryPath(), b, 0o644); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to store the registry", "games", err)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write(b)
}

// storedByID reads the registry as it is on disk, keyed by game id.
//
// An unreadable or absent registry gives an empty map: the save then behaves
// exactly as it did before there was anything to preserve.
func (h *Handlers) storedByID() map[string]Entry {
	// #nosec G304 -- registryPath() is the content root plus three constants.
	b, err := os.ReadFile(h.registryPath())
	if err != nil {
		return nil
	}
	items, ok := decodeRegistryItems(b, false)
	if !ok {
		return nil
	}
	out := make(map[string]Entry, len(items))
	for _, it := range items {
		out[strings.ToLower(it.GameID)] = it
	}
	return out
}

// mergeWithStored fills in, for every incoming entry, the fields its sender did
// not mention.
//
// Реализовано разбором ПОВЕРХ сохранённой записи, а не списком «полей, которые
// надо сберечь». Список пришлось бы дополнять при каждом новом поле, и забытое
// дополнение — это ровно та же тихая потеря данных, только в следующий раз.
// json.Unmarshal поверх готовой структуры трогает только те ключи, что есть в
// теле запроса, и потому сберегает всё остальное само.
func mergeWithStored(items []Entry, body []byte, stored map[string]Entry) []Entry {
	if len(stored) == 0 {
		return items
	}
	var raw struct {
		Items []json.RawMessage `json:"items"`
	}
	if err := json.Unmarshal(body, &raw); err != nil || len(raw.Items) != len(items) {
		// Разбор тела уже прошёл выше; сюда попадаем, только если форма
		// оказалась другой (например, элементы отфильтровались). Тогда лучше
		// сохранить как есть, чем сопоставить строки не с теми записями.
		return items
	}
	for i := range items {
		old, ok := stored[strings.ToLower(items[i].GameID)]
		if !ok {
			continue
		}
		merged := old
		if err := json.Unmarshal(raw.Items[i], &merged); err != nil {
			continue
		}
		// Порядок и нормализация посчитаны при разборе выше (в том числе
		// подстановка позиции для записи без "order"), поэтому берём их оттуда,
		// а не из сырого тела.
		merged.Order = items[i].Order
		items[i] = merged
	}
	return items
}

// Scan returns the registry list derived from the manifests directory without
// touching the stored registry.
func (h *Handlers) Scan(w http.ResponseWriter, _ *http.Request) {
	adminutil.WriteJSON(w, struct {
		Items []Entry `json:"items"`
	}{Items: h.FromManifests()})
}

// IconUpload saves the uploaded image as manifests/{gameId}/icon.png and
// returns its URL.
func (h *Handlers) IconUpload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	// Bound the whole request before parsing, exactly as the news asset upload
	// does: without this a client can make the process buffer and then read an
	// arbitrary amount of data.
	r.Body = http.MaxBytesReader(w, r.Body, media.MaxImageBytes+(1<<20))
	if err := r.ParseMultipartForm(16 << 20); err != nil { // 16MB
		http.Error(w, "request too large or malformed", http.StatusBadRequest)
		return
	}
	gid, ok := iconGameID(w, r)
	if !ok {
		return
	}
	data, ok := readIconUpload(w, r)
	if !ok {
		return
	}
	img, err := decodeIcon(data)
	if err != nil {
		http.Error(w, "unsupported image format", http.StatusBadRequest)
		return
	}
	// Ensure directory and save as PNG with fixed name icon.png. 0o755/0o644:
	// manifests/ is the tree nginx serves straight from disk.
	dir := filepath.Join(h.root, "manifests", gid)
	if err := os.MkdirAll(dir, 0o755); err != nil { // #nosec G301 -- see above
		adminutil.Fail(w, http.StatusInternalServerError, "failed to save icon", "games:icon", err)
		return
	}
	outPath := filepath.Join(dir, "icon.png")
	if !adminutil.EnsureWithin(filepath.Join(h.root, "manifests"), outPath) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := encodeIconPNG(outPath, img); err != nil {
		log.Printf("[games:icon] store %s: %v", outPath, err)
		http.Error(w, "failed to save icon", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok", "url": "/manifests/" + gid + "/icon.png"})
}

// iconGameID reads the gameId from the form and validates it BEFORE it is
// turned into a path: the EnsureWithin check in IconUpload used to run only
// after os.MkdirAll had already created (or traversed into) whatever directory
// the client asked for.
func iconGameID(w http.ResponseWriter, r *http.Request) (string, bool) {
	gid := strings.TrimSpace(r.FormValue("gameId"))
	if gid == "" {
		http.Error(w, "missing gameId", http.StatusBadRequest)
		return "", false
	}
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return "", false
	}
	return gid, true
}

// readIconUpload returns the uploaded bytes, bounded in both size and declared
// dimensions. It answers the client itself and reports ok=false.
func readIconUpload(w http.ResponseWriter, r *http.Request) ([]byte, bool) {
	file, _, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file", http.StatusBadRequest)
		return nil, false
	}
	defer func() { _ = file.Close() }()
	// io.ReadAll on a multipart part is unbounded by itself: the part may have
	// been spooled to disk and be far larger than the parse window, so the whole
	// file would land in RAM.
	data, err := io.ReadAll(io.LimitReader(file, media.MaxImageBytes+1))
	if err != nil {
		log.Printf("[games:icon] read upload: %v", err)
		http.Error(w, "failed to read upload", http.StatusInternalServerError)
		return nil, false
	}
	if len(data) > media.MaxImageBytes {
		http.Error(w, "image too large", http.StatusRequestEntityTooLarge)
		return nil, false
	}
	// Check the declared dimensions from the header before decoding: a tiny
	// PNG can announce 30000x30000 and cost gigabytes of pixel buffer.
	if err := media.CheckImageBounds(data); err != nil {
		http.Error(w, "image dimensions too large", http.StatusBadRequest)
		return nil, false
	}
	return data, true
}

// decodeIcon decodes PNG/JPEG. The format is not returned: the icon is always
// re-encoded as PNG.
func decodeIcon(data []byte) (image.Image, error) {
	img, _, err := image.Decode(bytes.NewReader(data))
	if err == nil {
		return img, nil
	}
	// The sniffing decoder failed; try the two supported formats explicitly.
	if im, e := png.Decode(bytes.NewReader(data)); e == nil {
		return im, nil
	}
	if im, e := jpeg.Decode(bytes.NewReader(data)); e == nil {
		return im, nil
	}
	return nil, err
}

// encodeIconPNG encodes into memory and writes atomically. os.Create truncates
// the live icon first, so a failed encode used to leave a zero-length or
// half-written file in a tree the public API serves — and the working icon was
// gone.
func encodeIconPNG(outPath string, img image.Image) error {
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(outPath, buf.Bytes(), 0o644)
}
