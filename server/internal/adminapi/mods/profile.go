package mods

import (
	"bufio"
	"errors"
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Importing an r2modman profile is what makes the migration off the current
// "one ZIP holds the game and its mods" builds cheap and safe.
//
// Every existing modded build on the server was assembled by hand in r2modman
// and carries the manager's own profile export inside it — mods.yml at the
// root of the game. That file names every installed mod and its exact version.
// The Lethal Company build ships 237 of them, and all 237 are still on
// Thunderstore today, so the current set can be rebuilt bit-for-bit rather
// than approximated by picking some published modpack that is merely similar.
//
// Two shapes are accepted, because r2modman writes both:
//
//	mods.yml     a bare list of items, each starting "- manifestVersion: 1"
//	export.r2x   a document with profileName + mods:, items starting "- name:"
//
// The parser is deliberately narrow rather than a general YAML reader. It
// keys on the indentation of the FIRST item marker and treats only that exact
// indent as an item boundary, which is what keeps the nested "dependencies:"
// list — whose entries are also YAML list items — from being mistaken for new
// mods.

var (
	itemMarkerRe = regexp.MustCompile(`^(\s*)-\s+(manifestVersion|name)\s*:`)
	nameRe       = regexp.MustCompile(`(?m)^\s*(?:-\s+)?name\s*:\s*(.+)$`)
	majorRe      = regexp.MustCompile(`(?m)^\s*major\s*:\s*(\d+)\s*$`)
	minorRe      = regexp.MustCompile(`(?m)^\s*minor\s*:\s*(\d+)\s*$`)
	patchRe      = regexp.MustCompile(`(?m)^\s*patch\s*:\s*(\d+)\s*$`)
	enabledRe    = regexp.MustCompile(`(?m)^\s*enabled\s*:\s*(\S+)\s*$`)
)

// ProfileMod is one entry of an imported profile.
type ProfileMod struct {
	// FullName is the Thunderstore dependency string, "Author-Mod-1.2.3".
	FullName string `json:"fullName"`
	Name     string `json:"name"`
	Version  string `json:"version"`
	Enabled  bool   `json:"enabled"`
}

// ParseProfile reads an r2modman mods.yml or export.r2x and returns the mods
// it lists, in file order.
//
// Disabled mods are returned too, with Enabled=false; the caller decides. A
// build skips them, because a mod the operator switched off in r2modman is one
// they deliberately did not want in the pack.
func ParseProfile(content string) ([]ProfileMod, error) {
	indent, ok := itemIndent(content)
	if !ok {
		return nil, errors.New("mods: not an r2modman profile: no item markers found")
	}

	blocks := splitItems(content, indent)
	if len(blocks) == 0 {
		return nil, errors.New("mods: r2modman profile lists no mods")
	}

	out := make([]ProfileMod, 0, len(blocks))
	for i, b := range blocks {
		m, err := parseItem(b)
		if err != nil {
			return nil, fmt.Errorf("mods: profile entry %d: %w", i+1, err)
		}
		out = append(out, m)
	}
	return out, nil
}

// EnabledDependencies returns the dependency strings of the enabled mods,
// ready to hand to ResolveList.
func EnabledDependencies(mods []ProfileMod) []string {
	out := make([]string, 0, len(mods))
	for _, m := range mods {
		if m.Enabled {
			out = append(out, m.FullName)
		}
	}
	return out
}

// itemIndent finds the indentation of the first item marker, which becomes the
// only indentation treated as an item boundary.
func itemIndent(content string) (string, bool) {
	sc := bufio.NewScanner(strings.NewReader(content))
	sc.Buffer(make([]byte, 0, 64<<10), 4<<20)
	for sc.Scan() {
		if m := itemMarkerRe.FindStringSubmatch(sc.Text()); m != nil {
			return m[1], true
		}
	}
	return "", false
}

// splitItems cuts the document into per-mod blocks at the given indent.
func splitItems(content, indent string) []string {
	prefix := indent + "-"
	var blocks []string
	var cur []string
	started := false

	sc := bufio.NewScanner(strings.NewReader(content))
	sc.Buffer(make([]byte, 0, 64<<10), 4<<20)
	for sc.Scan() {
		line := sc.Text()
		isMarker := strings.HasPrefix(line, prefix) &&
			itemMarkerRe.MatchString(line) &&
			itemMarkerRe.FindStringSubmatch(line)[1] == indent
		if isMarker {
			if started {
				blocks = append(blocks, strings.Join(cur, "\n"))
			}
			started = true
			cur = cur[:0]
		}
		if started {
			cur = append(cur, line)
		}
	}
	if started {
		blocks = append(blocks, strings.Join(cur, "\n"))
	}
	return blocks
}

// parseItem pulls the four fields that matter out of one block.
func parseItem(block string) (ProfileMod, error) {
	nm := nameRe.FindStringSubmatch(block)
	if nm == nil {
		return ProfileMod{}, errors.New("no name field")
	}
	name := strings.Trim(strings.TrimSpace(nm[1]), `"'`)
	if name == "" {
		return ProfileMod{}, errors.New("empty name field")
	}

	maj, majOK := firstInt(majorRe, block)
	mnr, minOK := firstInt(minorRe, block)
	pat, patOK := firstInt(patchRe, block)
	if !majOK || !minOK || !patOK {
		return ProfileMod{}, fmt.Errorf("mod %q has no complete version triple", name)
	}
	version := fmt.Sprintf("%d.%d.%d", maj, mnr, pat)

	// A missing "enabled" key means enabled: older exports omit it entirely,
	// and defaulting to disabled would quietly drop the whole pack.
	enabled := true
	if em := enabledRe.FindStringSubmatch(block); em != nil {
		enabled = !strings.EqualFold(strings.Trim(em[1], `"'`), "false")
	}

	return ProfileMod{
		FullName: name + "-" + version,
		Name:     name,
		Version:  version,
		Enabled:  enabled,
	}, nil
}

func firstInt(re *regexp.Regexp, block string) (int, bool) {
	m := re.FindStringSubmatch(block)
	if m == nil {
		return 0, false
	}
	n, err := strconv.Atoi(m[1])
	if err != nil {
		return 0, false
	}
	return n, true
}
