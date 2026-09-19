package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"ChillHub/server/internal/adminapi/builds"
)

func TestBackfillCommandReportsEveryVersionAndExitCode(t *testing.T) {
	root := t.TempDir()
	big := bytes.Repeat([]byte("0123456789abcdef"), builds.BlockSize/8) // два блока
	sum := sha256.Sum256(big)
	shaHex := hex.EncodeToString(sum[:])
	mustWrite(t, filepath.Join(root, "content", "g", "1.0.0", "files", "a.pak"), big)
	manifest := `{"version":"1.0.0","buildId":"b","gameId":"g","files":[{"path":"a.pak","size":` +
		strconv.Itoa(len(big)) + `,"blake3":"","sha256":"` + shaHex + `","executable":false}],"emptyDirs":[]}`
	mustWrite(t, filepath.Join(root, "manifests", "g", "1.0.0.json"), []byte(manifest))
	h := builds.New(root)

	var out bytes.Buffer
	if code := runBackfill(h, []string{"-game", "g"}, &out); code != 0 {
		t.Fatalf("exit %d, output:\n%s", code, out.String())
	}
	if !strings.Contains(out.String(), "g/1.0.0: 1 file(s)") {
		t.Fatalf("output does not name the version it filled:\n%s", out.String())
	}

	out.Reset()
	if code := runBackfill(h, nil, &out); code != 0 || !strings.Contains(out.String(), "skipped") {
		t.Fatalf("second run: exit %d, output:\n%s", code, out.String())
	}

	out.Reset()
	if code := runBackfill(h, []string{"-game", "nope"}, &out); code != 1 {
		t.Fatalf("unknown game: exit %d, want 1", code)
	}
	if code := runBackfill(h, []string{"-bogus"}, &out); code != 2 {
		t.Fatalf("bad flag: exit %d, want 2", code)
	}

	// Разошедшийся с манифестом файл — отказ с кодом 1, а не молчаливый пропуск.
	mustWrite(t, filepath.Join(root, "content", "g", "2.0.0", "files", "a.pak"), big)
	bad := strings.Replace(manifest, `"version":"1.0.0"`, `"version":"2.0.0"`, 1)
	bad = strings.Replace(bad, shaHex, strings.Repeat("0", 64), 1)
	mustWrite(t, filepath.Join(root, "manifests", "g", "2.0.0.json"), []byte(bad))
	out.Reset()
	if code := runBackfill(h, []string{"-version", "2.0.0"}, &out); code != 1 || !strings.Contains(out.String(), "FAILED") {
		t.Fatalf("mismatch: exit %d, output:\n%s", code, out.String())
	}
}

func mustWrite(t *testing.T, path string, data []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
}
