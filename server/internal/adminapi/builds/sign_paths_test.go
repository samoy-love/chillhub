package builds

import (
	"bytes"
	"crypto/ed25519"
	"encoding/base64"
	"strings"
	"testing"
)

// sign helper: produce a manifest signed with the given key, without going
// through signManifest (which now refuses invalid manifests - that refusal is
// exactly what several tests below need to bypass in order to prove the
// verification side is not the only line of defence).
func signWith(priv ed25519.PrivateKey, m manifest) manifest {
	m.Signature = ""
	sig := ed25519.Sign(priv, canonicalManifest(m))
	m.Signature = SignaturePrefix + base64.StdEncoding.EncodeToString(sig)
	return m
}

// A signature must cover the path the client actually writes to disk.
//
// Before this check the signed bytes were the *normalized* path while the
// client wrote the raw one, so every mutation below kept a valid signature
// while changing the destination file.
func TestNonCanonicalPathBreaksVerification(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}

	signed := signWith(priv, sampleManifest())
	if !verifyManifest(signed, pub) {
		t.Fatal("baseline manifest must verify")
	}

	// Every one of these normalizes back to "bin/game.exe", so the signed bytes
	// are bit-for-bit identical and the old code verified them happily - while
	// the client wrote the raw string to disk.
	mutations := []string{
		"/bin/game.exe",
		"bin\\game.exe",
		" bin/game.exe",
		"bin/game.exe ",
		"bin/game.exe/",
		"bin//game.exe",
	}
	for _, mutated := range mutations {
		m := signed
		m.Files = append([]manifestFile(nil), signed.Files...)
		m.Files[0].Path = mutated

		if !bytes.Equal(canonicalManifest(stripSig(m)), canonicalManifest(stripSig(signed))) {
			t.Fatalf("%q: test is not exercising the bug - canonical bytes already differ", mutated)
		}
		if verifyManifest(m, pub) {
			t.Errorf("path mutated to %q still verified", mutated)
		}
	}
}

func stripSig(m manifest) manifest {
	m.Signature = ""
	return m
}

func TestPathProblemRejectsDangerousPaths(t *testing.T) {
	bad := map[string]string{
		"parent escape":   "../evil.exe",
		"nested escape":   "data/../../evil.exe",
		"dot segment":     "data/./evil.exe",
		"absolute unix":   "/etc/passwd",
		"absolute win":    "C:/Windows/System32/evil.dll",
		"backslash":       "C:\\Windows\\evil.dll",
		"unc":             "\\\\server\\share\\evil.exe",
		"ntfs stream":     "game.exe:hidden.exe",
		"empty":           "",
		"leading slash":   "/game/app.exe",
		"trailing slash":  "game/app.exe/",
		"double slash":    "game//app.exe",
		"leading space":   " game/app.exe",
		"trailing space":  "game/app.exe ",
		"trailing dot":    "game/app.exe.",
		"tab in path":     "game/app\texe",
		"newline in path": "game/app\nexe",
		"device name":     "NUL",
		"device with ext": "data/CON.txt",
		"wildcard":        "data/*.dll",
		"too long":        strings.Repeat("a", maxPathLen+1),
	}
	for name, p := range bad {
		if why := pathProblem(p); why == "" {
			t.Errorf("%s: %q was accepted", name, p)
		}
	}

	good := []string{
		"game.exe",
		"bin/game.exe",
		"data/sub dir/pack.bin",
		"данные/игра.exe",
		"FreeTP/.hash",
		"a/b/c/d/e.dat",
	}
	for _, p := range good {
		if why := pathProblem(p); why != "" {
			t.Errorf("legitimate path %q rejected: %s", p, why)
		}
	}
}

// The signature is invariant to reordering (by design: the file list is
// sorted), so two entries for one path let an attacker pick which one the
// client keeps - it stores them in a map and the last write wins.
func TestDuplicatePathsAreRejected(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}

	cases := map[string]manifest{
		"exact duplicate": func() manifest {
			m := sampleManifest()
			m.Files = append(m.Files, manifestFile{Path: "bin/game.exe", Size: 1, Blake3: "eeee"})
			return m
		}(),
		"case-insensitive duplicate": func() manifest {
			m := sampleManifest()
			m.Files = append(m.Files, manifestFile{Path: "BIN/Game.EXE", Size: 1, Blake3: "eeee"})
			return m
		}(),
		"duplicate empty dir": func() manifest {
			m := sampleManifest()
			m.EmptyDirs = append(m.EmptyDirs, "LOGS")
			return m
		}(),
	}

	for name, m := range cases {
		if err := validateManifest(m); err == nil {
			t.Errorf("%s: validateManifest accepted it", name)
		}
		if verifyManifest(signWith(priv, m), pub) {
			t.Errorf("%s: a signed manifest with duplicates verified", name)
		}
	}

	// Control: swapping the two duplicate entries produces the same signature,
	// which is precisely why duplicates cannot be tolerated.
	dup := sampleManifest()
	dup.Files = append(dup.Files, manifestFile{Path: "bin/game.exe", Size: 999, Blake3: "eeee"})
	swapped := dup
	swapped.Files = []manifestFile{dup.Files[3], dup.Files[1], dup.Files[2], dup.Files[0]}
	if !bytes.Equal(canonicalManifest(dup), canonicalManifest(swapped)) {
		t.Fatal("expected the canonical form to be blind to the swap (that is the bug)")
	}
}

// A manifest that must not be signed must also not be accepted when someone
// else signs it: the guard sits on both sides of the key.
func TestDangerousManifestIsNeitherSignedNorVerified(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}

	for name, path := range map[string]string{
		"traversal": "../evil.exe",
		"absolute":  "C:/Windows/System32/evil.dll",
		"startup":   "../../../../AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.exe",
	} {
		m := sampleManifest()
		m.Files[0].Path = path

		if err := validateManifest(m); err == nil {
			t.Errorf("%s: validateManifest accepted %q", name, path)
		}

		// signManifest leaves the signature empty rather than authenticating it.
		signManifest(&m)
		if m.Signature != "" {
			t.Errorf("%s: signManifest produced a signature for %q", name, path)
		}

		if verifyManifest(signWith(priv, m), pub) {
			t.Errorf("%s: a correctly signed but dangerous manifest verified", name)
		}
	}
}

// TestManifestWithoutHashIsNotSigned pins the rule that a manifest entry must
// carry at least one hash.
//
// The launcher wraps its entire verification block in "if either hash is set",
// so an entry with both empty is not "integrity unknown" — it is integrity
// checking turned off for precisely the file chosen by whoever serves the
// manifest. Signing that would attest the absence was intended.
func TestManifestWithoutHashIsNotSigned(t *testing.T) {
	m := manifest{
		Version: "1.0.0",
		GameID:  "chill",
		Files: []manifestFile{
			{Path: "game.exe", Size: 1, Blake3: "aaaa"},
			{Path: "payload.exe", Size: 2},
		},
	}
	if err := validateManifest(m); err == nil {
		t.Fatal("a manifest with a hashless entry must be rejected")
	}

	// Signing must leave the field empty rather than emit a signature.
	t.Setenv(SigningKeyEnv, "")
	signManifest(&m)
	if m.Signature != "" {
		t.Fatalf("hashless manifest must stay unsigned, got %q", m.Signature)
	}

	// One hash is enough.
	m.Files[1].Sha256 = "bbbb"
	if err := validateManifest(m); err != nil {
		t.Fatalf("one hash must suffice: %v", err)
	}
}
