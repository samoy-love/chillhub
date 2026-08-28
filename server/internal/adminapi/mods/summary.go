package mods

import (
	"context"
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

// ModsGameSummary is one game whose active modpack fell behind Thunderstore.
type ModsGameSummary struct {
	GameID string `json:"gameId"`
	Title  string `json:"title"`
	Pack   string `json:"pack"`
	Active string `json:"active"`
	Latest string `json:"latest"`
}

// Summary is the whole answer.
type Summary struct {
	Launcher LauncherSummary   `json:"launcher"`
	Mods     []ModsGameSummary `json:"mods"`
	Pending  int               `json:"pending"`
}

// summaryCache keeps the last answer for summaryTTL.
type summaryCache struct {
	mu   sync.Mutex
	at   time.Time
	last *Summary
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

	out := &Summary{Launcher: h.launcherSummary(), Mods: h.modsSummary(ctx)}
	if out.Launcher.Pending {
		out.Pending++
	}
	out.Pending += len(out.Mods)

	h.sum.mu.Lock()
	h.sum.last = out
	h.sum.at = time.Now()
	h.sum.mu.Unlock()
	return out
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

// modsSummary lists games whose active modpack is behind Thunderstore.
func (h *Handlers) modsSummary(ctx context.Context) []ModsGameSummary {
	entries, err := h.games.All()
	if err != nil {
		log.Printf("[summary] реестр игр: %v", err)
		return nil
	}
	var out []ModsGameSummary
	for _, g := range entries {
		if g.Mods == nil || !g.Mods.Enabled {
			continue
		}
		active := h.builds.LatestVersion(builds.NamespaceMods, g.GameID)
		if active == "" {
			continue
		}
		checks := h.updateChecks(ctx, g.GameID, []VersionInfo{{Version: active}})
		if len(checks) == 0 {
			continue
		}
		c := checks[0]
		out = append(out, ModsGameSummary{
			GameID: g.GameID,
			Title:  g.Title,
			Pack:   c.Namespace + "/" + c.Name,
			Active: active,
			Latest: c.Latest,
		})
	}
	return out
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
