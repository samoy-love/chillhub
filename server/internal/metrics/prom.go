// Продуктовые счётчики поверх приёма телеметрии.
//
// Файл events.jsonl остаётся источником правды для админки: он хранит
// подробности, по нему считается сводка за произвольный период и его можно
// перечитать задним числом. Prometheus решает другую задачу — тренд и алерт, —
// и агрегировать ради него 32 МиБ NDJSON на каждый scrape нельзя: раз в 30
// секунд это постоянная нагрузка на диск ради чисел, которые дешевле
// посчитать один раз в момент приёма события.
//
// Поэтому счётчики инкрементируются здесь же, в Submit, и живут в памяти.
// Рестарт сервиса обнуляет их — для counter это штатная ситуация, rate() и
// increase() распознают сброс. Абсолютные суммы «за всё время» по-прежнему
// живут в файле, а не тут.

package metrics

import (
	"strings"

	"ChillHub/server/internal/promexp"
)

// Buckets for how long an install or an update took. A game is gigabytes over
// a home connection, so the interesting range is minutes to an hour, not the
// milliseconds a request histogram cares about.
var syncDurationBuckets = []float64{10, 30, 60, 180, 600, 1800, 3600}

// Product holds every metric derived from launcher telemetry.
//
// Naming follows the exposition conventions: base unit in the name (_bytes,
// _seconds), _total on counters. The "mode" label on updates is what makes the
// launcher's whole reason for existing measurable — see Record.
type Product struct {
	starts    *promexp.Counter
	installs  *promexp.Counter
	updates   *promexp.Counter
	launches  *promexp.Counter
	errors    *promexp.Counter
	events    *promexp.Counter
	rejected  *promexp.Counter
	synthetic *promexp.Counter
	bytes     *promexp.Counter
	fullBytes *promexp.Counter
	files     *promexp.Counter
	fullFiles *promexp.Counter
	integrity *promexp.Counter
	mismatch  *promexp.Counter
	installMs *promexp.Histogram
	updateMs  *promexp.Histogram
}

// NewProduct registers the launcher metrics in reg.
func NewProduct(reg *promexp.Registry) *Product {
	return &Product{
		starts: reg.NewCounter("chillhub_launcher_starts_total",
			"Запуски лаунчера, по версии клиента", "app_version"),
		installs: reg.NewCounter("chillhub_game_installs_total",
			"Установки игры с нуля: сколько дошло до конца, сколько сорвалось", "game", "result"),
		updates: reg.NewCounter("chillhub_game_updates_total",
			"Обновления игры; mode=diff — скачаны не все файлы сборки", "game", "result", "mode"),
		launches: reg.NewCounter("chillhub_game_launches_total",
			"Запуски игры из лаунчера", "game"),
		errors: reg.NewCounter("chillhub_client_errors_total",
			"Ошибки на стороне клиента по коду классификации", "code"),
		events: reg.NewCounter("chillhub_telemetry_events_total",
			"Принятые события телеметрии по типу", "event"),
		rejected: reg.NewCounter("chillhub_telemetry_rejected_total",
			"Отклонённые обращения к /metrics/report", "reason"),
		synthetic: reg.NewCounter("chillhub_telemetry_synthetic_total",
			"События служебных прогонов: приняты, но в продуктовые счётчики не идут", "event"),
		bytes: reg.NewCounter("chillhub_downloaded_bytes_total",
			"Фактически скачано клиентами байт", "game"),
		fullBytes: reg.NewCounter("chillhub_build_full_bytes_total",
			"Сколько байт весила бы та же операция при полной загрузке сборки", "game"),
		files: reg.NewCounter("chillhub_downloaded_files_total",
			"Фактически скачано файлов", "game"),
		fullFiles: reg.NewCounter("chillhub_build_files_total",
			"Сколько файлов в сборке целиком", "game"),
		integrity: reg.NewCounter("chillhub_integrity_checks_total",
			"Проверки целостности установленной игры", "game", "result"),
		mismatch: reg.NewCounter("chillhub_hash_mismatches_total",
			"Файлы, у которых хеш разошёлся с манифестом", "game"),
		installMs: reg.NewHistogram("chillhub_install_duration_seconds",
			"Длительность успешной установки", syncDurationBuckets, "game"),
		updateMs: reg.NewHistogram("chillhub_update_duration_seconds",
			"Длительность успешного обновления", syncDurationBuckets, "game"),
	}
}

// Reject counts a submission that never became an event.
func (p *Product) Reject(reason string) {
	if p == nil {
		return
	}
	p.rejected.Inc(reason)
}

// Record folds one accepted event into the counters.
//
// # Экономия трафика
//
// Смысл лаунчера в том, что обновление качает изменившиеся файлы, а не сборку
// целиком. Чтобы это было видно числом, а не на слово, клиент присылает
// одновременно фактический объём (Bytes/FilesDownloaded) и объём той же
// операции при полной загрузке (FullBytes/FilesTotal). Экономия в Grafana
// считается как 1 - downloaded/full; отдельной метрики «экономия» нет
// намеренно: доля, посчитанная на клиенте, не складывается между установками,
// а два счётчика складываются.
func (p *Product) Record(ev Event) {
	if p == nil {
		return
	}
	// Событие своего же автотеста принято и лежит в файле, но продуктовым
	// числом не становится: и сводка админки, и графики отвечают на вопрос
	// «сколько играют люди». Отдельный счётчик оставлен, чтобы прогон,
	// проверяющий приём событий, всё же было видно. См. synthetic.go.
	if isSynthetic(ev.InstallID) {
		p.synthetic.Inc(labelOr(ev.Event, "unknown"))
		return
	}
	game := gameLabel(ev.GameID)
	p.events.Inc(labelOr(ev.Event, "unknown"))
	p.recordVolume(ev, game)
	p.recordKind(ev, game)
}

// recordVolume folds the "how much traffic did this cost" counters. Every field
// is optional: an older launcher reports none of them, and a zero must not be
// added as if it were a measurement.
func (p *Product) recordVolume(ev Event, game string) {
	if ev.Bytes > 0 {
		p.bytes.Add(float64(ev.Bytes), game)
	}
	if ev.FullBytes > 0 {
		p.fullBytes.Add(float64(ev.FullBytes), game)
	}
	if ev.FilesDownloaded > 0 {
		p.files.Add(float64(ev.FilesDownloaded), game)
	}
	if ev.FilesTotal > 0 {
		p.fullFiles.Add(float64(ev.FilesTotal), game)
	}
	if ev.HashMismatches > 0 {
		p.mismatch.Add(float64(ev.HashMismatches), game)
	}
}

// recordKind folds the counters that depend on the event kind.
func (p *Product) recordKind(ev Event, game string) {
	result := labelOr(ev.Result, "unknown")
	switch ev.Event {
	case "launcher_start":
		p.starts.Inc(versionLabel(ev.AppVersion))
	case "game_install":
		p.installs.Inc(game, result)
		observeDuration(p.installMs, ev, game)
	case "game_update":
		p.updates.Inc(game, result, updateMode(ev))
		observeDuration(p.updateMs, ev, game)
	case "game_launch":
		p.launches.Inc(game)
	case "integrity_check":
		p.integrity.Inc(game, result)
	case "error":
		p.errors.Inc(labelOr(ev.ErrorCode, "unknown"))
	}
}

// observeDuration records how long a successful operation took. A failed or
// cancelled run is deliberately not observed: it stopped early, so its duration
// would drag the quantiles towards a number nobody waited for.
func observeDuration(h *promexp.Histogram, ev Event, game string) {
	if ev.Result == "ok" && ev.DurationMs > 0 {
		h.Observe(float64(ev.DurationMs)/1000, game)
	}
}

// updateMode says whether the update actually behaved like a diff.
//
// It is derived from the file counts rather than trusted as a client-set flag:
// the flag would say what the launcher intended, the counts say what the user
// actually paid for in traffic. "unknown" covers old clients that report
// neither — they must not silently inflate either bucket.
func updateMode(ev Event) string {
	if ev.FilesTotal <= 0 {
		return "unknown"
	}
	if ev.FilesDownloaded < ev.FilesTotal {
		return "diff"
	}
	return "full"
}

// gameLabel keeps a client-supplied game id out of the label set unless it
// looks like a real identifier. The registry caps cardinality anyway, but a
// junk id would burn one of those slots and show up on the dashboard as if it
// were a game.
func gameLabel(id string) string {
	id = strings.TrimSpace(id)
	if id == "" {
		return "none"
	}
	if len(id) > 40 || !isSafeLabel(id) {
		return promexp.OverflowValue
	}
	return id
}

func versionLabel(v string) string {
	v = strings.TrimSpace(v)
	if v == "" {
		return "unknown"
	}
	if len(v) > 24 || !isSafeLabel(v) {
		return promexp.OverflowValue
	}
	return v
}

func labelOr(v, def string) string {
	v = strings.TrimSpace(v)
	if v == "" {
		return def
	}
	if len(v) > 40 || !isSafeLabel(v) {
		return promexp.OverflowValue
	}
	return v
}

// isSafeLabel accepts the shape of every identifier this project generates:
// letters, digits and the few separators used in game ids, versions and error
// codes. Anything else — spaces, quotes, non-ASCII — is a value nobody meant to
// aggregate by.
func isSafeLabel(s string) bool {
	for i := range len(s) {
		c := s[i]
		switch {
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z', c >= '0' && c <= '9':
		case c == '-', c == '_', c == '.', c == '+':
		default:
			return false
		}
	}
	return true
}
