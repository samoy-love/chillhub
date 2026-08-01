package builds

import (
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"log"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// SigningKeyEnv is the environment variable holding the base64 encoded Ed25519
// private key used to sign manifests. It accepts either a 64 byte private key
// (seed+public, as produced by ed25519.GenerateKey) or a bare 32 byte seed.
const SigningKeyEnv = "MANIFEST_SIGNING_KEY"

// SignaturePrefix marks a real Ed25519 signature. Anything that does not carry
// this prefix (notably the historical "dev-mock-signature" placeholder) is
// treated by clients as "unsigned".
const SignaturePrefix = "ed25519:"

// canonicalVersion is the first line of every canonical representation. It is
// part of the signed bytes, so bumping it invalidates old signatures on
// purpose: a client that knows only v1 cannot be tricked into accepting a
// manifest canonicalized under different rules.
const canonicalVersion = "chillhub-manifest-v1"

// canonicalManifest renders a manifest into stable bytes for signing.
//
// The layout is line based, LF separated, with a trailing LF:
//
//	chillhub-manifest-v1
//	version:<version>
//	gameId:<gameId>
//	buildId:<buildId>
//	files:<count>
//	file:<path>\t<size>\t<blake3>\t<sha256>\t<0|1 executable>   (sorted by path)
//	dirs:<count>
//	dir:<path>                                                  (sorted)
//
// Notes:
//   - createdAt is deliberately excluded: it is metadata, not content, and
//     including it would make two identical builds produce different bytes.
//   - the signature field is of course excluded.
//   - paths are normalized (backslashes to slashes, no leading/trailing slash)
//     and hashes are lowercased, so that cosmetic differences in how a manifest
//     was produced cannot change the signed bytes.
//   - the file list is sorted, so reordering entries in the JSON does not
//     change the signature.
func canonicalManifest(m manifest) []byte {
	var sb strings.Builder
	sb.WriteString(canonicalVersion)
	sb.WriteByte('\n')
	sb.WriteString("version:" + m.Version + "\n")
	sb.WriteString("gameId:" + m.GameID + "\n")
	sb.WriteString("buildId:" + m.BuildID + "\n")

	files := make([]manifestFile, len(m.Files))
	copy(files, m.Files)
	for i := range files {
		files[i].Path = canonPath(files[i].Path)
		files[i].Blake3 = strings.ToLower(strings.TrimSpace(files[i].Blake3))
		files[i].Sha256 = strings.ToLower(strings.TrimSpace(files[i].Sha256))
	}
	sort.Slice(files, func(i, j int) bool {
		if files[i].Path != files[j].Path {
			return files[i].Path < files[j].Path
		}
		return files[i].Blake3 < files[j].Blake3
	})
	sb.WriteString("files:" + strconv.Itoa(len(files)) + "\n")
	for _, f := range files {
		exec := "0"
		if f.Executable {
			exec = "1"
		}
		sb.WriteString("file:")
		sb.WriteString(f.Path)
		sb.WriteByte('\t')
		sb.WriteString(strconv.FormatInt(f.Size, 10))
		sb.WriteByte('\t')
		sb.WriteString(f.Blake3)
		sb.WriteByte('\t')
		sb.WriteString(f.Sha256)
		sb.WriteByte('\t')
		sb.WriteString(exec)
		sb.WriteByte('\n')
	}

	dirs := make([]string, 0, len(m.EmptyDirs))
	for _, d := range m.EmptyDirs {
		dirs = append(dirs, canonPath(d))
	}
	sort.Strings(dirs)
	sb.WriteString("dirs:" + strconv.Itoa(len(dirs)) + "\n")
	for _, d := range dirs {
		sb.WriteString("dir:" + d + "\n")
	}
	return []byte(sb.String())
}

// canonPath normalizes a manifest path to the form used for signing.
//
// Normalization alone is NOT a defence: for years the signature covered the
// normalized path while the client wrote the raw one, so " /game//app.exe\ "
// and "game/app.exe" shared a signature but not a destination. canonPath now
// only describes what a path must already look like - see validateManifest.
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
// looser one decides what lands on disk - which is the whole bug class this
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
	// The signed bytes and the bytes used on disk must be the same bytes.
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
		// "foo" are one file but two different signed strings.
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

// validateManifest rejects manifests that must never be signed.
//
// Signing is an assertion that the client may write exactly these files. A
// manifest whose paths are ambiguous (non-canonical, duplicated) makes that
// assertion meaningless: several different sets of files satisfy one signature.
func validateManifest(m manifest) error {
	seen := make(map[string]int, len(m.Files))
	for i, f := range m.Files {
		if why := pathProblem(f.Path); why != "" {
			return errors.New("file #" + strconv.Itoa(i) + " " + strconv.Quote(f.Path) + ": " + why)
		}
		// A record with no hash at all is not "unknown integrity", it is
		// integrity checking switched off for exactly that file: the client
		// wraps its whole verification block in "if either hash is set".
		// Signing such a manifest would attest that the absence is intended.
		if strings.TrimSpace(f.Blake3) == "" && strings.TrimSpace(f.Sha256) == "" {
			return errors.New("file #" + strconv.Itoa(i) + " " + strconv.Quote(f.Path) + ": no hash to verify against")
		}

		// Case-insensitive: the client stores files on a case-insensitive
		// filesystem and keys its map the same way, so "A.dll" and "a.dll"
		// are one destination but two manifest entries - and whichever comes
		// last wins, without changing the signature.
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

var (
	signingKeyOnce sync.Once
	signingKey     ed25519.PrivateKey
)

// loadSigningKey reads and caches the private key from the environment. A
// missing or malformed key is reported loudly once: silently shipping unsigned
// builds is exactly the failure mode this feature exists to prevent.
func loadSigningKey() ed25519.PrivateKey {
	signingKeyOnce.Do(func() {
		raw := strings.TrimSpace(os.Getenv(SigningKeyEnv))
		if raw == "" {
			log.Printf("SECURITY: %s is not set - manifests will be published UNSIGNED. "+
				"Generate a key pair with: go run ./internal/adminapi/builds/keygen", SigningKeyEnv)
			return
		}
		key, err := ParseSigningKey(raw)
		if err != nil {
			log.Printf("SECURITY: %s is invalid (%v) - manifests will be published UNSIGNED", SigningKeyEnv, err)
			return
		}
		signingKey = key
		log.Printf("manifest signing enabled, public key: %s", base64.StdEncoding.EncodeToString(key.Public().(ed25519.PublicKey)))
	})
	return signingKey
}

// ParseSigningKey decodes a base64 Ed25519 private key (64 byte full key or 32
// byte seed). Both standard and URL-safe base64 are accepted, with or without
// padding, because keys travel through systemd unit files and shells.
func ParseSigningKey(s string) (ed25519.PrivateKey, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil, errors.New("empty key")
	}
	var (
		b   []byte
		err error
	)
	for _, enc := range []*base64.Encoding{
		base64.StdEncoding, base64.RawStdEncoding,
		base64.URLEncoding, base64.RawURLEncoding,
	} {
		if b, err = enc.DecodeString(s); err == nil {
			break
		}
	}
	if err != nil {
		return nil, errors.New("not valid base64")
	}
	switch len(b) {
	case ed25519.PrivateKeySize:
		return ed25519.PrivateKey(b), nil
	case ed25519.SeedSize:
		return ed25519.NewKeyFromSeed(b), nil
	default:
		return nil, errors.New("expected 32 (seed) or 64 (private key) bytes, got " + strconv.Itoa(len(b)))
	}
}

// SignManifest fills in the signature field of a manifest. Without a configured
// key the field is left empty, which clients read as "unsigned".
func signManifest(m *manifest) {
	m.Signature = ""
	key := loadSigningKey()
	if key == nil {
		return
	}
	// Refuse to sign an ambiguous manifest. Leaving it unsigned is the safe
	// failure: clients in strict mode reject it outright, and clients in
	// compatibility mode are no worse off than with an unsigned build. Signing
	// it would be worse than useless - it would authenticate an ambiguity.
	if err := validateManifest(*m); err != nil {
		log.Printf("SECURITY: refusing to sign manifest gameId=%q buildId=%q: %v", m.GameID, m.BuildID, err)
		return
	}
	sig := ed25519.Sign(key, canonicalManifest(*m))
	m.Signature = SignaturePrefix + base64.StdEncoding.EncodeToString(sig)
}

// verifyManifest checks a manifest signature against a public key. It exists
// for tests and for operational tooling; the launcher does the same check with
// its own implementation.
func verifyManifest(m manifest, pub ed25519.PublicKey) bool {
	if !strings.HasPrefix(m.Signature, SignaturePrefix) {
		return false
	}
	// Same rule as the client: an ambiguous manifest is invalid regardless of
	// whose key signed it.
	if err := validateManifest(m); err != nil {
		return false
	}
	sig, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(m.Signature, SignaturePrefix))
	if err != nil {
		return false
	}
	unsigned := m
	unsigned.Signature = ""
	return ed25519.Verify(pub, canonicalManifest(unsigned), sig)
}
