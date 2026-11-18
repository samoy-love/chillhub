package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
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

func TestBackfillCommandOnlyAddsSmallPacksWithTheFlag(t *testing.T) {
	root := t.TempDir()
	var files []map[string]any
	for i := range 8 {
		path := fmt.Sprintf("cfg-%d.ini", i)
		data := []byte(path)
		sum := sha256.Sum256(data)
		mustWrite(t, filepath.Join(root, "content", "_mods", "g", "1.0.0", "files", path), data)
		files = append(files, map[string]any{"path": path, "size": len(data), "sha256": hex.EncodeToString(sum[:])})
	}
	directory := filepath.Join(root, "manifests", "_mods", "g")
	path := filepath.Join(directory, "1.0.0.json")
	m, err := json.Marshal(map[string]any{"version": "1.0.0", "gameId": "g", "files": files})
	if err != nil {
		t.Fatal(err)
	}
	mustWrite(t, path, m)
	latest := []byte("{ \"version\": \"1.1.0\" }\n")
	mustWrite(t, filepath.Join(directory, "1.1.0.json"), []byte(strings.Replace(string(m), "1.0.0", "1.1.0", 1)))
	mustWrite(t, filepath.Join(directory, "latest.json"), latest)
	h := builds.New(root)

	var output bytes.Buffer
	if code := runBackfill(h, []string{"-game", "g", "-version", "1.0.0"}, &output); code != 0 ||
		!strings.Contains(output.String(), "skipped: nothing to add") {
		t.Fatalf("block mode: exit %d, output %s", code, output.String())
	}
	if data, err := os.ReadFile(path); err != nil || !bytes.Equal(data, m) {
		t.Fatalf("block mode changed the small-file manifest: %v, %v", data, err)
	}

	args := []string{"--small-packs", "-game", "g", "-version", "1.0.0"}
	output.Reset()
	if code := runBackfill(h, args, &output); code != 0 ||
		!strings.Contains(output.String(), "_mods/g/1.0.0: 1 small pack(s), 8 file(s), manifest updated") {
		t.Fatalf("small packs mode: exit %d, output %s", code, output.String())
	}
	if data, err := os.ReadFile(path); err != nil || !bytes.Contains(data, []byte(`"smallPacks"`)) {
		t.Fatalf("pack metadata absent: %v", err)
	}
	if data, err := os.ReadFile(filepath.Join(directory, "latest.json")); err != nil || !bytes.Equal(data, latest) {
		t.Fatalf("small packs mode changed latest: %v, %v", data, err)
	}
	output.Reset()
	if code := runBackfill(h, args, &output); code != 0 || !strings.Contains(output.String(), "manifest unchanged") {
		t.Fatalf("repeated small packs mode: exit %d, output %s", code, output.String())
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
