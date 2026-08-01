package builds

import (
	"bytes"
	"crypto/ed25519"
	"encoding/base64"
	"strings"
	"testing"
)

func sampleManifest() manifest {
	return manifest{
		Version:   "1.2.3",
		BuildID:   "b-42",
		GameID:    "chill",
		CreatedAt: "2026-01-01T00:00:00Z",
		Files: []manifestFile{
			{Path: "bin/game.exe", Size: 100, Blake3: "aaaa", Sha256: "bbbb", Executable: true},
			{Path: "data/pak0.dat", Size: 200, Blake3: "cccc"},
			{Path: "readme.txt", Size: 3, Blake3: "dddd"},
		},
		EmptyDirs: []string{"logs", "saves"},
		Signature: "dev-mock-signature",
	}
}

func TestCanonicalManifestIsDeterministic(t *testing.T) {
	a := canonicalManifest(sampleManifest())
	b := canonicalManifest(sampleManifest())
	if !bytes.Equal(a, b) {
		t.Fatalf("canonical form is not stable:\n%s\nvs\n%s", a, b)
	}
	if !bytes.HasPrefix(a, []byte(canonicalVersion+"\n")) {
		t.Fatalf("canonical form must start with the scheme version, got %q", string(a[:32]))
	}
}

func TestCanonicalManifestIgnoresOrderAndCosmetics(t *testing.T) {
	base := sampleManifest()

	shuffled := sampleManifest()
	shuffled.Files = []manifestFile{shuffled.Files[2], shuffled.Files[0], shuffled.Files[1]}
	shuffled.EmptyDirs = []string{"saves", "logs"}
	if !bytes.Equal(canonicalManifest(base), canonicalManifest(shuffled)) {
		t.Fatal("reordering files or empty dirs changed the canonical form")
	}

	// Path separators, stray slashes, hash case and the signature field itself
	// must not leak into the signed bytes.
	cosmetic := sampleManifest()
	cosmetic.Files[0].Path = "bin\\game.exe"
	cosmetic.Files[0].Blake3 = "AAAA"
	cosmetic.Files[1].Path = "/data//pak0.dat"
	cosmetic.EmptyDirs = []string{"saves/", "/logs"}
	cosmetic.Signature = "whatever"
	if !bytes.Equal(canonicalManifest(base), canonicalManifest(cosmetic)) {
		t.Fatalf("cosmetic differences changed the canonical form:\n%s\nvs\n%s",
			canonicalManifest(base), canonicalManifest(cosmetic))
	}

	// createdAt is metadata and must not be signed.
	other := sampleManifest()
	other.CreatedAt = "2030-06-06T06:06:06Z"
	if !bytes.Equal(canonicalManifest(base), canonicalManifest(other)) {
		t.Fatal("createdAt leaked into the canonical form")
	}
}

func TestCanonicalManifestDetectsRealChanges(t *testing.T) {
	base := canonicalManifest(sampleManifest())
	cases := map[string]func(*manifest){
		"version":    func(m *manifest) { m.Version = "1.2.4" },
		"gameId":     func(m *manifest) { m.GameID = "other" },
		"buildId":    func(m *manifest) { m.BuildID = "b-43" },
		"size":       func(m *manifest) { m.Files[0].Size = 101 },
		"hash":       func(m *manifest) { m.Files[0].Blake3 = "aaab" },
		"sha256":     func(m *manifest) { m.Files[0].Sha256 = "bbbc" },
		"executable": func(m *manifest) { m.Files[1].Executable = true },
		"extra file": func(m *manifest) { m.Files = append(m.Files, manifestFile{Path: "evil.exe"}) },
		"empty dir":  func(m *manifest) { m.EmptyDirs = append(m.EmptyDirs, "tmp") },
	}
	for name, mutate := range cases {
		m := sampleManifest()
		mutate(&m)
		if bytes.Equal(base, canonicalManifest(m)) {
			t.Errorf("change of %s did not affect the canonical form", name)
		}
	}
}

func TestSignAndVerify(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	m := sampleManifest()
	m.Signature = ""
	sig := ed25519.Sign(priv, canonicalManifest(m))
	m.Signature = SignaturePrefix + base64.StdEncoding.EncodeToString(sig)

	if !verifyManifest(m, pub) {
		t.Fatal("a freshly signed manifest failed verification")
	}

	// Tampering with the content must break verification.
	bad := m
	bad.Files = append([]manifestFile(nil), m.Files...)
	bad.Files[0].Blake3 = "deadbeef"
	if verifyManifest(bad, pub) {
		t.Fatal("tampered manifest passed verification")
	}

	// A different key must not validate.
	otherPub, _, _ := ed25519.GenerateKey(nil)
	if verifyManifest(m, otherPub) {
		t.Fatal("manifest validated under a foreign key")
	}

	// The legacy placeholder must not be mistaken for a signature.
	legacy := m
	legacy.Signature = "dev-mock-signature"
	if verifyManifest(legacy, pub) {
		t.Fatal("dev-mock-signature was accepted as a real signature")
	}
}

func TestParseSigningKey(t *testing.T) {
	_, priv, _ := ed25519.GenerateKey(nil)
	seed := priv.Seed()

	for name, enc := range map[string]string{
		"full key std":  base64.StdEncoding.EncodeToString(priv),
		"full key raw":  base64.RawStdEncoding.EncodeToString(priv),
		"seed std":      base64.StdEncoding.EncodeToString(seed),
		"seed url safe": base64.RawURLEncoding.EncodeToString(seed),
	} {
		got, err := ParseSigningKey(enc)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if !bytes.Equal(got, priv) {
			t.Fatalf("%s: parsed key differs from the original", name)
		}
	}

	for _, bad := range []string{"", "not base64 !!!", base64.StdEncoding.EncodeToString([]byte("short"))} {
		if _, err := ParseSigningKey(bad); err == nil {
			t.Fatalf("expected an error for %q", bad)
		}
	}
}

func TestWriteManifestWithoutKeyLeavesSignatureEmpty(t *testing.T) {
	// No MANIFEST_SIGNING_KEY is set in tests, so publication must still work
	// and simply produce an unsigned manifest (old clients keep working).
	h := New(t.TempDir())
	m := sampleManifest()
	_, b, err := h.writeManifest(m, true)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(b), "dev-mock-signature") {
		t.Fatal("the mock signature is still being written")
	}
	if !strings.Contains(string(b), `"signature": ""`) {
		t.Fatalf("expected an empty signature without a key, got:\n%s", b)
	}
}
