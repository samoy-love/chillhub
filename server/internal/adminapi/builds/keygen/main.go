// Command keygen generates an Ed25519 key pair for manifest signing.
//
// Usage (from the server module root):
//
//	go run ./internal/adminapi/builds/keygen
//
// It prints the private key (to be put into MANIFEST_SIGNING_KEY on the
// server, never into the repository) and the public key (to be embedded into
// the launcher as ManifestSignature.PublicKeyBase64).
package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"fmt"
	"os"
)

func main() {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		fmt.Fprintln(os.Stderr, "keygen failed:", err)
		os.Exit(1)
	}
	fmt.Println("# private key - set as MANIFEST_SIGNING_KEY on the build server, keep secret")
	fmt.Println("MANIFEST_SIGNING_KEY=" + base64.StdEncoding.EncodeToString(priv))
	fmt.Println()
	fmt.Println("# public key - embed into the launcher (ManifestSignature.PublicKeyBase64)")
	fmt.Println(base64.StdEncoding.EncodeToString(pub))
}
