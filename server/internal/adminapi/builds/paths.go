package builds

import (
	"errors"
	"strconv"
	"strings"
)

// canonPath normalizes a manifest path to its canonical form.
//
// Normalization alone is NOT a defence: for years the manifest carried the
// normalized path while the client wrote the raw one, so " /game//app.exe\ "
// and "game/app.exe" described one destination through two different strings.
// canonPath now only describes what a path must already look like — see
// validateManifest, which rejects anything that is not already in this form.
func canonPath(p string) string {
	p = strings.ReplaceAll(strings.TrimSpace(p), "\\", "/")
	for strings.Contains(p, "//") {
		p = strings.ReplaceAll(p, "//", "/")
	}
	return strings.Trim(p, "/")
}

// maxPathLen bounds a single manifest path. Nothing legitimate comes close.
const maxPathLen = 1024

// reservedNames are Windows device names: a file called "NUL" is not a file.
var reservedNames = map[string]bool{
	"CON": true, "PRN": true, "AUX": true, "NUL": true,
	"COM1": true, "COM2": true, "COM3": true, "COM4": true, "COM5": true,
	"COM6": true, "COM7": true, "COM8": true, "COM9": true,
	"LPT1": true, "LPT2": true, "LPT3": true, "LPT4": true, "LPT5": true,
	"LPT6": true, "LPT7": true, "LPT8": true, "LPT9": true,
}

// pathProblem reports why a manifest path is unacceptable, or "" if it is fine.
//
// The rules are deliberately identical to the client side
// (launcher/ChillHub/../updater/ManifestPath.cs). If the two ever disagree, the
// looser one decides what lands on disk — which is the whole bug class this
// function exists to close.
func pathProblem(p string) string {
	if p == "" {
		return "empty path"
	}
	if len(p) > maxPathLen {
		return "path too long"
	}
	for _, r := range p {
		if r < 0x20 || r == 0x7F {
			return "control character in path"
		}
	}
	if strings.ContainsRune(p, ':') {
		return "colon in path (drive or NTFS stream)"
	}
	if strings.ContainsRune(p, '\\') {
		return "backslash in path"
	}
	// The path that is validated and the path used on disk must be one string.
	if p != canonPath(p) {
		return "path is not in canonical form"
	}
	for _, seg := range strings.Split(p, "/") {
		if seg == "" {
			return "empty path segment"
		}
		if seg == "." || seg == ".." {
			return "path segment " + seg
		}
		// Windows silently strips trailing dots and spaces, so "foo." and
		// "foo" are one file but two different manifest entries.
		if strings.HasSuffix(seg, ".") || strings.HasSuffix(seg, " ") || strings.HasPrefix(seg, " ") {
			return "path segment with leading/trailing space or dot"
		}
		if strings.ContainsAny(seg, "*?\"<>|") {
			return "invalid character in path segment"
		}
		stem := seg
		if i := strings.IndexByte(seg, '.'); i >= 0 {
			stem = seg[:i]
		}
		if reservedNames[strings.ToUpper(stem)] {
			return "reserved device name " + stem
		}
	}
	return ""
}

// validateManifest rejects manifests that must never be published.
//
// A manifest is an assertion that the client may write exactly these files. A
// manifest whose paths are ambiguous (non-canonical, duplicated) makes that
// assertion meaningless: several different sets of files satisfy it.
func validateManifest(m manifest) error {
	seen := make(map[string]int, len(m.Files))
	for i, f := range m.Files {
		if why := pathProblem(f.Path); why != "" {
			return errors.New("file #" + strconv.Itoa(i) + " " + strconv.Quote(f.Path) + ": " + why)
		}

		// A record with no hash at all is not "unknown integrity", it is
		// integrity checking switched off for exactly that file: the client
		// wraps its whole verification block in "if either hash is set".
		if strings.TrimSpace(f.Blake3) == "" && strings.TrimSpace(f.Sha256) == "" {
			return errors.New("file #" + strconv.Itoa(i) + " " + strconv.Quote(f.Path) + ": no hash to verify against")
		}

		// Case-insensitive: the client stores files on a case-insensitive
		// filesystem and keys its map the same way, so "A.dll" and "a.dll"
		// are one destination but two manifest entries — and whichever comes
		// last wins.
		key := strings.ToLower(f.Path)
		if prev, dup := seen[key]; dup {
			return errors.New("duplicate path " + strconv.Quote(f.Path) + " (entries #" +
				strconv.Itoa(prev) + " and #" + strconv.Itoa(i) + ")")
		}
		seen[key] = i
	}

	dirSeen := make(map[string]int, len(m.EmptyDirs))
	for i, d := range m.EmptyDirs {
		if why := pathProblem(d); why != "" {
			return errors.New("emptyDir #" + strconv.Itoa(i) + " " + strconv.Quote(d) + ": " + why)
		}
		key := strings.ToLower(d)
		if prev, dup := dirSeen[key]; dup {
			return errors.New("duplicate emptyDir " + strconv.Quote(d) + " (entries #" +
				strconv.Itoa(prev) + " and #" + strconv.Itoa(i) + ")")
		}
		dirSeen[key] = i
	}
	return nil
}
