package adminutil

import (
	"sort"
	"strconv"
	"strings"
)

// CompareVersions orders version labels the way a human reads them, returning
// -1, 0 or 1.
//
// Plain string ordering is wrong for versions and the mistake is silent until a
// release crosses a decimal boundary: "1.1.10" sorts BEFORE "1.1.9" because '1'
// < '9'. The numeric dot-separated components are therefore compared as
// numbers, a missing component counts as 0 ("1.2" == "1.2.0"), and a
// pre-release suffix ("1.2.0-rc1") sorts before the release it precedes, as
// semver requires. Components that are not numbers at all fall back to a string
// comparison so that unexpected labels still produce a stable order.
func CompareVersions(a, b string) int {
	aCore, aPre := splitPreRelease(a)
	bCore, bPre := splitPreRelease(b)

	aParts := strings.Split(aCore, ".")
	bParts := strings.Split(bCore, ".")
	n := len(aParts)
	if len(bParts) > n {
		n = len(bParts)
	}
	for i := 0; i < n; i++ {
		ap, bp := "", ""
		if i < len(aParts) {
			ap = aParts[i]
		}
		if i < len(bParts) {
			bp = bParts[i]
		}
		if c := compareComponent(ap, bp); c != 0 {
			return c
		}
	}

	switch {
	case aPre == bPre:
		return 0
	case aPre == "": // release beats its own pre-releases
		return 1
	case bPre == "":
		return -1
	case aPre < bPre:
		return -1
	default:
		return 1
	}
}

// splitPreRelease cuts "1.2.0-rc1" into ("1.2.0", "rc1").
func splitPreRelease(v string) (core, pre string) {
	v = strings.TrimSpace(v)
	if i := strings.IndexAny(v, "-+"); i >= 0 {
		return v[:i], v[i+1:]
	}
	return v, ""
}

// compareComponent compares one dot-separated component. An empty component
// (the version simply had fewer of them) counts as 0.
func compareComponent(a, b string) int {
	an, aok := parseComponent(a)
	bn, bok := parseComponent(b)
	if aok && bok {
		switch {
		case an < bn:
			return -1
		case an > bn:
			return 1
		default:
			return 0
		}
	}
	switch {
	case a == b:
		return 0
	case a < b:
		return -1
	default:
		return 1
	}
}

func parseComponent(s string) (uint64, bool) {
	if s == "" {
		return 0, true
	}
	n, err := strconv.ParseUint(s, 10, 64)
	if err != nil {
		return 0, false
	}
	return n, true
}

// SortVersionsDesc sorts versions newest first, in place.
func SortVersionsDesc(v []string) {
	sort.SliceStable(v, func(i, j int) bool { return CompareVersions(v[i], v[j]) > 0 })
}

// MaxVersion returns the newest of the given versions, or "" for an empty list.
func MaxVersion(v []string) string {
	best := ""
	for i, s := range v {
		if i == 0 || CompareVersions(s, best) > 0 {
			best = s
		}
	}
	return best
}
