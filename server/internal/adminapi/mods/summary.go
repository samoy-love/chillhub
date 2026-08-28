package mods

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminapi/builds"
	"ChillHub/server/internal/adminutil"
)

// ЧТО ЖДЁТ ДЕЙСТВИЯ — ВИДНО ДО ТОГО, КАК ОТКРОЕШЬ ВКЛАДКУ.
//
// Две вещи в панели решает человек и только человек: какую сборку лаунчера
// сделать активной и когда пересобрать модпак под вышедшее обновление. Обе
// узнавались одинаково — открыть вкладку, выбрать игру, сравнить таблицу
// глазами. То есть узнать о них можно было только случайно.
//
// Эта сводка отвечает на оба вопроса одним запросом при загрузке панели, и по
// ней зажигаются значки на вкладках.

const (
	// summaryTTL — как долго сводка считается свежей.
	//
	// Половина её (обновления модпаков) стоит запроса к Thunderstore на каждый
	// собранный пакет, а панель перезапрашивают на каждое действие. Десять
	// минут — это «узнать о вышедшем обновлении в тот же рабочий подход», а не
	// «ходить в сеть по кнопке».
	summaryTTL = 10 * time.Minute

	// summaryTimeout бережёт панель от того, чтобы ждать Thunderstore.
	summaryTimeout = 30 * time.Second
)

// LauncherSummary tells whether the newest uploaded launcher build is the one
// players actually receive.
type LauncherSummary struct {
	Active  string `json:"active"`
	Newest  string `json:"newest"`
	Pending bool   `json:"pending"`
}

// ModsGameSummary is the state of one game's active modpack against Thunderstore.
//
// СТРОКА ЕСТЬ У КАЖДОЙ ИГРЫ С МОДАМИ, а не только у отставшей. Пока сюда
// попадали одни отставшие, «всё свежо» и «проверка не сработала» выглядели
// одинаково — пустым списком, — и отличить их можно было только чтением логов
// сервера. Теперь молчание значит ровно одно: игр с модами нет вовсе.
type ModsGameSummary struct {
	GameID string `json:"gameId"`
	Title  string `json:"title"`
	Pack   string `json:"pack"`
	Active string `json:"active"`
	Latest string `json:"latest"`

	// Behind — активная сборка отстала от Thunderstore. Только по нему считается
	// «здесь ждут действия»: остальные поля описывают состояние, а не задачу.
	Behind bool `json:"behind"`

	// Deprecated — пакет помечен автором как устаревший. Версия при этом может
	// совпадать: делать с такой игрой что-то надо, но не «пересобрать пакет».
	Deprecated bool `json:"deprecated"`

	// Error — почему состояние неизвестно: сеть, отказ Thunderstore, источник не
	// с Thunderstore. Пустая строка — проверка прошла.
	Error string `json:"error,omitempty"`
}

// Summary is the whole answer.
type Summary struct {
	Launcher LauncherSummary   `json:"launcher"`
	Mods     []ModsGameSummary `json:"mods"`
	Pending  int               `json:"pending"`
}

// summaryCache keeps the Thunderstore half of the answer for summaryTTL.
type summaryCache struct {
	mu   sync.Mutex
	at   time.Time
	last []ModsGameSummary
}

// SummaryHandler answers GET /admin/api/summary.
//
// Never fails: a panel that cannot draw its badges must still draw everything
// else. Whatever could not be computed comes back as "nothing pending", and the
// reason goes to the log.
func (h *Handlers) SummaryHandler(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodGet) {
		return
	}
	force := r.URL.Query().Get("force") == "1"
	adminutil.WriteJSON(w, h.summary(r.Context(), force))
}

func (h *Handlers) summary(ctx context.Context, force bool) *Summary {
	// КЕШИРУЕТСЯ ТОЛЬКО ТО, ЧТО ХОДИТ В СЕТЬ.
	//
	// Половина про лаунчер — это два чтения с диска, и держать её десять минут
	// значит врать ровно там, где ответ дешевле всего: сборку загрузили, а
	// панель ещё треть часа показывает прежнюю самую свежую версию. Раньше
	// кешировался весь ответ целиком, и «загрузил — не вижу» было штатным
	// поведением.
	out := &Summary{Launcher: h.launcherSummary(), Mods: h.modsSummaryCached(ctx, force)}
	if out.Launcher.Pending {
		out.Pending++
	}
	for _, m := range out.Mods {
		if m.Behind || m.Deprecated {
			out.Pending++
		}
	}
	return out
}

// modsSummaryCached keeps the Thunderstore half for summaryTTL.
func (h *Handlers) modsSummaryCached(ctx context.Context, force bool) []ModsGameSummary {
	if !force {
		h.sum.mu.Lock()
		fresh := h.sum.last != nil && time.Since(h.sum.at) < summaryTTL
		last := h.sum.last
		h.sum.mu.Unlock()
		if fresh {
			return last
		}
	}

	ctx, cancel := context.WithTimeout(ctx, summaryTimeout)
	defer cancel()
	mods := h.modsSummary(ctx)

	h.sum.mu.Lock()
	h.sum.last = mods
	h.sum.at = time.Now()
	h.sum.mu.Unlock()
	return mods
}

// launcherSummary compares the active launcher build with the newest uploaded
// one. Local files only — no network, so the badge appears instantly.
func (h *Handlers) launcherSummary() LauncherSummary {
	active := h.builds.LatestVersion(builds.NamespaceGame, "launcher")
	versions, err := h.builds.ListPublished(builds.NamespaceGame, "launcher")
	if err != nil {
		log.Printf("[summary] версии лаунчера: %v", err)
		return LauncherSummary{Active: active}
	}
	newest := newestVersion(versions)
	return LauncherSummary{
		Active:  active,
		Newest:  newest,
		Pending: newest != "" && active != "" && newest != active,
	}
}

// modsSummary reports every game with a built modpack: what is installed, what
// Thunderstore has now, and whether that is a reason to do something.
//
// Пустой срез, а не nil: в JSON это [] вместо null, и читающему не приходится
// гадать, «моды не проверялись» или «игр с модами нет».
func (h *Handlers) modsSummary(ctx context.Context) []ModsGameSummary {
	out := []ModsGameSummary{}
	entries, err := h.games.All()
	if err != nil {
		log.Printf("[summary] реестр игр: %v", err)
		return out
	}
	for _, g := range entries {
		if g.Mods == nil || !g.Mods.Enabled {
			continue
		}
		active := h.builds.LatestVersion(builds.NamespaceMods, g.GameID)
		if active == "" {
			continue
		}

		row := ModsGameSummary{GameID: g.GameID, Title: g.Title, Active: active}
		st, err := h.packStatus(ctx, g.GameID, active)
		if err != nil {
			// Причина едет игроку панели, а не только в лог: «состояние
			// неизвестно» и «всё свежо» — разные новости.
			row.Error = err.Error()
			out = append(out, row)
			continue
		}

		row.Pack = st.Namespace + "/" + st.Name
		row.Latest = st.Latest
		row.Behind = st.Latest != "" && st.Latest != st.Installed
		row.Deprecated = st.Deprecated
		out = append(out, row)
	}
	return out
}

// packState is what Thunderstore says about one built modpack version.
type packState struct {
	Namespace  string
	Name       string
	Installed  string
	Latest     string
	Deprecated bool
}

// packStatus asks Thunderstore about one built version.
//
// Отдельно от updateChecks: та отвечает на вопрос «что в таблице версий стоит
// пересобрать» и молчит про свежее, а сводке нужно состояние КАЖДОЙ игры,
// включая «всё в порядке».
func (h *Handlers) packStatus(ctx context.Context, gid, version string) (packState, error) {
	src, err := h.builder.ReadSource(gid, version)
	if err != nil {
		return packState{}, fmt.Errorf("нет записи об источнике сборки: %w", err)
	}
	if src.Kind != SourceThunderstore {
		return packState{}, errors.New("сборка собрана не из пакета Thunderstore")
	}

	ns, name, installed, ok := SplitDependency(version)
	if !ok {
		return packState{}, fmt.Errorf("имя версии %q не разбирается на пакет и номер", version)
	}

	p, err := h.builder.Client.GetPackage(ctx, ns, name)
	if err != nil {
		return packState{}, fmt.Errorf("Thunderstore не ответил про %s-%s: %w", ns, name, err)
	}

	return packState{
		Namespace:  ns,
		Name:       name,
		Installed:  installed,
		Latest:     p.Latest.VersionNumber,
		Deprecated: p.IsDeprecated,
	}, nil
}

// newestVersion picks the highest version of a launcher build list.
//
// Сравнение по числам, а не по строкам: строковое «1.6.10» меньше «1.6.9», и
// значок про «есть версия свежее» загорался бы ровно наоборот. Всё, что не
// разбирается на числа, уходит в конец — такие версии лаунчер и не публикует.
func newestVersion(versions []string) string {
	if len(versions) == 0 {
		return ""
	}
	sorted := append([]string(nil), versions...)
	sort.Slice(sorted, func(i, j int) bool { return lessVersion(sorted[j], sorted[i]) })
	return sorted[0]
}

func lessVersion(a, b string) bool {
	an, bn := versionParts(a), versionParts(b)
	for i := 0; i < len(an) || i < len(bn); i++ {
		av, bv := 0, 0
		if i < len(an) {
			av = an[i]
		}
		if i < len(bn) {
			bv = bn[i]
		}
		if av != bv {
			return av < bv
		}
	}
	return a < b
}

func versionParts(v string) []int {
	parts := strings.Split(strings.TrimSpace(v), ".")
	out := make([]int, 0, len(parts))
	for _, p := range parts {
		n, err := strconv.Atoi(p)
		if err != nil {
			return out
		}
		out = append(out, n)
	}
	return out
}
