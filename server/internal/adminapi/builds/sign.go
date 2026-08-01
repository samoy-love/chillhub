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
func canonPath(p string) string {
	p = strings.ReplaceAll(strings.TrimSpace(p), "\\", "/")
	for strings.Contains(p, "//") {
		p = strings.ReplaceAll(p, "//", "/")
	}
	return strings.Trim(p, "/")
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
	sig, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(m.Signature, SignaturePrefix))
	if err != nil {
		return false
	}
	unsigned := m
	unsigned.Signature = ""
	return ed25519.Verify(pub, canonicalManifest(unsigned), sig)
}
