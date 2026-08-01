package builds

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"testing"
)

// TestGenerateNonBMPVector prints a signed manifest whose paths sit outside the
// Basic Multilingual Plane. It is a generator, not an assertion: the output is
// pasted into the C# test suite so that both implementations are pinned to the
// same bytes. Run with:
//
//	go test ./internal/adminapi/builds -run TestGenerateNonBMPVector -v
func TestGenerateNonBMPVector(t *testing.T) {
	seed := make([]byte, ed25519.SeedSize)
	for i := range seed {
		seed[i] = byte(i + 1)
	}
	key := ed25519.NewKeyFromSeed(seed)

	m := manifest{
		Version:   "1.0.0",
		BuildID:   "b-order",
		GameID:    "chill",
		CreatedAt: "2026-01-01T00:00:00Z",
		Files: []manifestFile{
			// U+E000 is a BMP private-use character, the emoji is U+1F600.
			// In UTF-8 U+E000 (0xEE...) sorts BEFORE the emoji (0xF0...);
			// in UTF-16 the emoji's lead surrogate 0xD83D sorts BEFORE 0xE000.
			// The two orders are opposite, which is the whole point of this vector.
			{Path: "\U0001F600.txt", Size: 2, Blake3: "bbbb", Executable: false},
			{Path: ".txt", Size: 1, Blake3: "aaaa", Executable: false},
		},
		EmptyDirs: []string{"\U0001F600dir", "dir"},
	}

	if err := validateManifest(m); err != nil {
		t.Fatalf("vector must be valid: %v", err)
	}

	t.Logf("canonical bytes:\n%s", canonicalManifest(m))
	sig := ed25519.Sign(key, canonicalManifest(m))
	m.Signature = SignaturePrefix + base64.StdEncoding.EncodeToString(sig)

	b, _ := json.MarshalIndent(m, "", "  ")
	fmt.Printf("PUBKEY=%s\n", base64.StdEncoding.EncodeToString(key.Public().(ed25519.PublicKey)))
	fmt.Printf("MANIFEST=%s\n", string(b))

	if !verifyManifest(m, key.Public().(ed25519.PublicKey)) {
		t.Fatal("self-verification failed")
	}
}
