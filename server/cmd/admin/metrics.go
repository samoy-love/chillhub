package main

import (
	"net/http"
	"strings"

	"ChillHub/server/internal/httpx"
	"ChillHub/server/internal/maintenance"
	"ChillHub/server/internal/promexp"
)

// adminMetrics holds what the admin process itself can see happening.
//
// The launcher's own numbers (installs, updates, traffic saved) arrive as
// telemetry and live in internal/metrics; these four are actions that happen
// HERE and have no other witness: a build going live, maintenance mode being
// switched on, a user writing in, someone signing into the panel. Each of them
// explains a bend in the product graphs — "installs dropped at 19:40" and
// "maintenance was enabled at 19:38" only answer each other when both are on
// the same time axis.
type adminMetrics struct {
	reg *promexp.Registry

	feedback    *promexp.Counter
	maintenance *promexp.Counter
	activations *promexp.Counter
	logins      *promexp.Counter
}

func newAdminMetrics(reg *promexp.Registry, mt *maintenance.Store) *adminMetrics {
	m := &adminMetrics{
		reg: reg,
		feedback: reg.NewCounter("chillhub_feedback_submissions_total",
			"Обращения обратной связи из лаунчера", "result"),
		maintenance: reg.NewCounter("chillhub_maintenance_changes_total",
			"Переключения режима техработ через админку", "action", "result"),
		activations: reg.NewCounter("chillhub_build_activations_total",
			"Активации версии сборки через админку", "result"),
		logins: reg.NewCounter("chillhub_admin_logins_total",
			"Попытки входа в админку", "result"),
	}
	// Читается на каждом scrape, а не хранится копией: состояние живёт в файле
	// и меняется в том числе по расписанию (окно техработ истекает само),
	// поэтому копия в памяти отставала бы ровно там, где это важно.
	reg.NewGaugeFunc("chillhub_maintenance_enabled",
		"1, когда режим техработ сейчас действует", func() float64 {
			if mt.Current().Enabled {
				return 1
			}
			return 0
		})
	return m
}

// count wraps a handler so that a successful call bumps c.
//
// The result label comes from the status code, not from the fact the handler
// was reached: "someone tried to activate a build and got 500" is a different
// event from "a build went live", and a single counter that conflates them
// would make the dashboard lie exactly when something is broken.
func (m *adminMetrics) count(c *promexp.Counter, h http.HandlerFunc, labels ...string) http.HandlerFunc {
	if m == nil || c == nil {
		return h
	}
	return httpx.Observe(h, func(status int) {
		res := "ok"
		if status >= 400 {
			res = "fail"
		}
		c.Inc(append(append([]string(nil), labels...), res)...)
	})
}

// routeLabels splits the registered ServeMux patterns into exact paths and
// prefixes, which is exactly the distinction ServeMux itself makes: a pattern
// ending in "/" is a subtree.
func routeLabels(paths []string) ([]string, []string) {
	var exact, prefixes []string
	for _, p := range paths {
		if strings.HasSuffix(p, "/") {
			prefixes = append(prefixes, p)
			continue
		}
		exact = append(exact, p)
	}
	return exact, prefixes
}
