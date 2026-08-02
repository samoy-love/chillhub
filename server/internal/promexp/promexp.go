// Package promexp is a dependency-free exporter of metrics in the Prometheus
// text format (version 0.0.4).
//
// # Why not the official client library
//
// prometheus/client_golang pulls in a dozen transitive modules for what this
// server needs: a handful of counters, two gauges and two histograms rendered
// into a text document. The rest of this repository is deliberately built on a
// short dependency list (see go.mod), and a metrics endpoint is not a good
// reason to triple it. Everything the format requires — HELP/TYPE headers,
// escaped label values, cumulative histogram buckets — fits in this file.
//
// # Cardinality is bounded on purpose
//
// Some label values arrive from the launcher (gameId, error codes), that is,
// from outside. An unbounded label turns a stranger's typo — or a hostile
// client — into unbounded memory here AND into a permanently damaged TSDB on
// the Prometheus side, which is far harder to undo than a lost data point. So
// every family caps the number of distinct label sets it will track: past the
// cap the observation is still counted, but under the label value "other".
// Losing the breakdown of the 201st game is acceptable; losing the server is
// not.
//
// # Concurrency
//
// Counters and gauges are updated from request handlers, i.e. from many
// goroutines, and read by the scrape handler. Every family carries its own
// mutex: the sections are a few instructions long, and a single global lock
// would put an unrelated download and a scrape in each other's way.
package promexp

import (
	"io"
	"math"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// ContentType is the media type of the exposition format this package writes.
const ContentType = "text/plain; version=0.0.4; charset=utf-8"

// MaxSeries caps the distinct label sets one metric family keeps; see the
// package doc. 200 is far above the real number of games and error codes and
// far below anything that could hurt.
const MaxSeries = 200

// OverflowValue replaces every label of an observation that arrives after the
// family hit MaxSeries.
const OverflowValue = "other"

// DefaultBuckets are latency buckets in seconds. They stop at 10s on purpose:
// this server also streams multi-gigabyte uploads, and one bucket per order of
// magnitude past that would only add noise to a histogram whose question is
// "are the small requests still fast".
var DefaultBuckets = []float64{0.005, 0.025, 0.1, 0.5, 1, 2.5, 10}

type series struct {
	labels []string
	value  float64
	// histogram state; nil for counters and gauges
	counts []uint64
	sum    float64
	count  uint64
}

type family struct {
	name    string
	help    string
	typ     string
	labels  []string
	buckets []float64
	// fn serves gauges whose value is read at scrape time rather than set.
	fn func() float64

	mu     sync.Mutex
	order  []string
	series map[string]*series
}

// Registry holds every metric of one process and renders them on demand.
type Registry struct {
	mu       sync.Mutex
	families []*family
	names    map[string]struct{}
}

// New returns an empty registry.
func New() *Registry { return &Registry{names: make(map[string]struct{})} }

func (r *Registry) add(f *family) *family {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, dup := r.names[f.name]; dup {
		// A duplicate name produces a document Prometheus refuses to parse, and
		// it can only come from a programming mistake at start-up — fail loudly
		// while there is still a human watching.
		panic("promexp: metric registered twice: " + f.name)
	}
	r.names[f.name] = struct{}{}
	f.series = make(map[string]*series)
	r.families = append(r.families, f)
	return f
}

// Counter is a monotonically increasing value.
type Counter struct{ f *family }

// Gauge is a value that goes up and down.
type Gauge struct{ f *family }

// Histogram observes a distribution into fixed buckets.
type Histogram struct{ f *family }

// NewCounter registers a counter. By convention its name ends in _total.
func (r *Registry) NewCounter(name, help string, labels ...string) *Counter {
	return &Counter{f: r.add(&family{name: name, help: help, typ: "counter", labels: labels})}
}

// NewGauge registers a gauge whose value is set by the caller.
func (r *Registry) NewGauge(name, help string, labels ...string) *Gauge {
	return &Gauge{f: r.add(&family{name: name, help: help, typ: "gauge", labels: labels})}
}

// NewGaugeFunc registers a label-less gauge read at scrape time. It suits
// values that already live somewhere else (a config flag, a queue length) and
// would otherwise need a second copy kept in sync by hand.
func (r *Registry) NewGaugeFunc(name, help string, fn func() float64) {
	r.add(&family{name: name, help: help, typ: "gauge", fn: fn})
}

// NewHistogram registers a histogram. Nil buckets means DefaultBuckets.
func (r *Registry) NewHistogram(name, help string, buckets []float64, labels ...string) *Histogram {
	if len(buckets) == 0 {
		buckets = DefaultBuckets
	}
	b := append([]float64(nil), buckets...)
	sort.Float64s(b)
	return &Histogram{f: r.add(&family{name: name, help: help, typ: "histogram", labels: labels, buckets: b})}
}

// get resolves (and creates, within the cap) the series for these label values.
// The family lock must be held.
func (f *family) get(values []string) *series {
	if len(values) != len(f.labels) {
		// Wrong arity is a coding error. Rather than panic inside a request
		// handler — dropping a real user's response over a metric — the
		// observation is folded into the overflow series, where it is visible
		// but harmless.
		values = overflow(len(f.labels))
	}
	key := strings.Join(values, "\x00")
	if s, ok := f.series[key]; ok {
		return s
	}
	if len(f.series) >= MaxSeries {
		values = overflow(len(f.labels))
		key = strings.Join(values, "\x00")
		if s, ok := f.series[key]; ok {
			return s
		}
	}
	s := &series{labels: append([]string(nil), values...)}
	if f.typ == "histogram" {
		s.counts = make([]uint64, len(f.buckets))
	}
	f.series[key] = s
	f.order = append(f.order, key)
	return s
}

func overflow(n int) []string {
	out := make([]string, n)
	for i := range out {
		out[i] = OverflowValue
	}
	return out
}

// Add increases the counter by v (negative values are ignored: a counter that
// goes backwards makes rate() report a reset that never happened).
func (c *Counter) Add(v float64, labelValues ...string) {
	if v < 0 || math.IsNaN(v) {
		return
	}
	c.f.mu.Lock()
	c.f.get(labelValues).value += v
	c.f.mu.Unlock()
}

// Inc adds one.
func (c *Counter) Inc(labelValues ...string) { c.Add(1, labelValues...) }

// Set writes the gauge value.
func (g *Gauge) Set(v float64, labelValues ...string) {
	g.f.mu.Lock()
	g.f.get(labelValues).value = v
	g.f.mu.Unlock()
}

// Observe records one value.
func (h *Histogram) Observe(v float64, labelValues ...string) {
	if math.IsNaN(v) {
		return
	}
	h.f.mu.Lock()
	s := h.f.get(labelValues)
	s.count++
	s.sum += v
	for i, ub := range h.f.buckets {
		if v <= ub {
			s.counts[i]++
		}
	}
	h.f.mu.Unlock()
}

// Write renders the whole registry. Output is deterministic: families in
// registration order, series sorted by label values — otherwise every scrape
// would produce a different byte stream and a test could only assert on
// fragments.
func (r *Registry) Write(w io.Writer) error {
	r.mu.Lock()
	families := append([]*family(nil), r.families...)
	r.mu.Unlock()

	var b strings.Builder
	for _, f := range families {
		b.WriteString("# HELP ")
		b.WriteString(f.name)
		b.WriteByte(' ')
		b.WriteString(escapeHelp(f.help))
		b.WriteByte('\n')
		b.WriteString("# TYPE ")
		b.WriteString(f.name)
		b.WriteByte(' ')
		b.WriteString(f.typ)
		b.WriteByte('\n')

		if f.fn != nil {
			b.WriteString(f.name)
			b.WriteByte(' ')
			b.WriteString(formatFloat(f.fn()))
			b.WriteByte('\n')
			continue
		}

		f.mu.Lock()
		keys := append([]string(nil), f.order...)
		sort.Strings(keys)
		for _, k := range keys {
			s := f.series[k]
			switch f.typ {
			case "histogram":
				var cum uint64
				for i, ub := range f.buckets {
					cum = s.counts[i]
					writeSample(&b, f.name+"_bucket", f.labels, s.labels, "le", formatFloat(ub), float64(cum))
				}
				writeSample(&b, f.name+"_bucket", f.labels, s.labels, "le", "+Inf", float64(s.count))
				writeSample(&b, f.name+"_sum", f.labels, s.labels, "", "", s.sum)
				writeSample(&b, f.name+"_count", f.labels, s.labels, "", "", float64(s.count))
			default:
				writeSample(&b, f.name, f.labels, s.labels, "", "", s.value)
			}
		}
		f.mu.Unlock()
	}
	_, err := io.WriteString(w, b.String())
	return err
}

func writeSample(b *strings.Builder, name string, names, values []string, extraName, extraValue string, v float64) {
	b.WriteString(name)
	if len(names) > 0 || extraName != "" {
		b.WriteByte('{')
		for i := range names {
			if i > 0 {
				b.WriteByte(',')
			}
			b.WriteString(names[i])
			b.WriteString(`="`)
			b.WriteString(escapeLabel(values[i]))
			b.WriteString(`"`)
		}
		if extraName != "" {
			if len(names) > 0 {
				b.WriteByte(',')
			}
			b.WriteString(extraName)
			b.WriteString(`="`)
			b.WriteString(escapeLabel(extraValue))
			b.WriteString(`"`)
		}
		b.WriteByte('}')
	}
	b.WriteByte(' ')
	b.WriteString(formatFloat(v))
	b.WriteByte('\n')
}

// formatFloat renders a sample value. Whole numbers come out without a decimal
// point (counters are the common case) and infinities use the spelling the
// exposition format requires, not Go's "+Inf"/"-Inf" default for %v.
func formatFloat(v float64) string {
	switch {
	case math.IsInf(v, 1):
		return "+Inf"
	case math.IsInf(v, -1):
		return "-Inf"
	case math.IsNaN(v):
		return "NaN"
	case v == math.Trunc(v) && math.Abs(v) < 1e15:
		return strconv.FormatInt(int64(v), 10)
	}
	return strconv.FormatFloat(v, 'g', -1, 64)
}

func escapeHelp(s string) string {
	return strings.NewReplacer(`\`, `\\`, "\n", `\n`).Replace(s)
}

func escapeLabel(s string) string {
	return strings.NewReplacer(`\`, `\\`, `"`, `\"`, "\n", `\n`).Replace(s)
}

// Handler serves the registry. It answers GET/HEAD only: a metrics endpoint
// that accepts POST invites confusion with the launcher's /metrics/report
// ingest, which is a different thing on a different port.
func (r *Registry) Handler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		if req.Method != http.MethodGet && req.Method != http.MethodHead {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		w.Header().Set("Content-Type", ContentType)
		w.Header().Set("Cache-Control", "no-store")
		if req.Method == http.MethodHead {
			w.WriteHeader(http.StatusOK)
			return
		}
		_ = r.Write(w)
	})
}
