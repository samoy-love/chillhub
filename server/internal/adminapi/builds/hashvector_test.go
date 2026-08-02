package builds

import (
	"crypto/sha256"
	"encoding/hex"
	"testing"

	"github.com/zeebo/blake3"
)

// CROSS-LANGUAGE HASH CONTRACT.
//
// The server writes blake3/sha256 into every published manifest; the launcher
// re-computes them from the files on disk and compares. Those are two
// independent implementations — Go (github.com/zeebo/blake3) here, C# (the
// Blake3 NuGet package) there — and either side can drift on an upgrade.
//
// A drift does not surface as an error anywhere. Every installed launcher simply
// finds that every file mismatches and re-downloads whole games: gigabytes of
// traffic, noticed by users rather than by us.
//
// The 1 MiB ramp below is duplicated verbatim in the client test
// launcher/tests/ChillHub.Tests/HashVectorTests.cs. It is long enough to exercise
// the multi-block SIMD path, unlike the short vectors from the specification.
// Change the constant only in both files at once, and only after genuinely
// recomputing the reference.
func TestBlake3MatchesTheLauncherImplementation(t *testing.T) {
	ramp := make([]byte, 1<<20)
	for i := range ramp {
		ramp[i] = byte(i)
	}

	const want = "64479cf7293960210547db8d982359e0c4ce054525ed7086cf93030828fc0533"
	sum := blake3.Sum256(ramp)
	if got := hex.EncodeToString(sum[:]); got != want {
		t.Fatalf("blake3(1 MiB ramp) = %s, want %s\n"+
			"The server and the launcher no longer agree on hashes: every client "+
			"would re-download every game. Check which side changed its blake3 "+
			"library before touching this constant.", got, want)
	}
}

// The official BLAKE3 vectors, kept next to the cross-language one: if both this
// and the client's copy fail together, the library is wrong; if only one fails,
// the two sides have drifted apart.
func TestBlake3OfficialVectors(t *testing.T) {
	for _, tc := range []struct{ in, want string }{
		{"", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"},
		{"abc", "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"},
	} {
		sum := blake3.Sum256([]byte(tc.in))
		if got := hex.EncodeToString(sum[:]); got != tc.want {
			t.Errorf("blake3(%q) = %s, want %s", tc.in, got, tc.want)
		}
	}
}

// SHA-256 is the second hash in every manifest entry, and the launcher accepts a
// file only when both agree. Pinning it guards against the same class of drift.
func TestSha256OfficialVector(t *testing.T) {
	sum := sha256.Sum256(nil)
	const want = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
	if got := hex.EncodeToString(sum[:]); got != want {
		t.Fatalf("sha256(empty) = %s, want %s", got, want)
	}
}
