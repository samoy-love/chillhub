package builds

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// TestNotifyPublishedArgs pins the exact flags sent to lib/notify.sh. The
// contract there rejects anything it doesn't recognize (die_reject on an
// unknown flag, on a kind without --version, …), so a typo here is a silent
// dropped notification on the server, not a compile error.
func TestNotifyPublishedArgs(t *testing.T) {
	got := notifyPublishedArgs("chillhub-installer", "1.3.2", "/tmp/cl.txt")
	want := []string{
		"--mode", "local",
		"--source", "local",
		"--kind", "published",
		"--app", "chillhub-installer",
		"--version", "1.3.2",
		"--changelog-file", "/tmp/cl.txt",
	}
	if len(got) != len(want) {
		t.Fatalf("args = %q, want %q", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("args = %q, want %q", got, want)
		}
	}
}

// TestNotifyPublishedArgsWithoutChangelog covers the fallback path taken when
// staging the changelog temp file fails: the notification must still carry
// the fields lib/notify.sh requires for kind=published, just without the link.
func TestNotifyPublishedArgsWithoutChangelog(t *testing.T) {
	got := notifyPublishedArgs("chillhub-installer", "1.3.2", "")
	for _, a := range got {
		if a == "--changelog-file" {
			t.Fatalf("args = %q: --changelog-file must be absent, not empty, when there is no file", got)
		}
	}
}

// TestNotifyPublishedInvokesScript proves the end-to-end wiring — env var
// override, subprocess invocation, changelog staging — against a stand-in for
// lib/notify.sh, without touching a real deploy-kit installation. bash is a
// real requirement of notifyPublished itself (lib/notify.sh is a bash
// script), so this probes for it rather than assuming: on a Windows dev box
// without Git Bash on PATH this skips instead of failing for an environment
// reason unrelated to the code under test.
func TestNotifyPublishedInvokesScript(t *testing.T) {
	if _, err := exec.LookPath("bash"); err != nil {
		t.Skip("no bash on PATH — notifyPublished shells out to a bash script")
	}

	dir := t.TempDir()
	logPath := filepath.Join(dir, "invocations.log")
	script := filepath.Join(dir, "fake-notify.sh")
	// Records its own argv and the changelog file's contents, exactly what a
	// real integration bug here would get wrong: an argument in the wrong
	// place, or a changelog file that doesn't exist by the time the script
	// tries to read it.
	fake := "#!/usr/bin/env bash\n" +
		"{ echo \"ARGS:$*\"; for a in \"$@\"; do " +
		"if [ \"$prev\" = \"--changelog-file\" ]; then echo \"CHANGELOG:$(cat \"$a\")\"; fi; prev=\"$a\"; done; } >> " +
		shellQuote(logPath) + "\n"
	if err := os.WriteFile(script, []byte(fake), 0o700); err != nil {
		t.Fatalf("write fake script: %v", err)
	}

	t.Setenv(notifyScriptEnv, script)
	notifyPublished(context.Background(), "chillhub-installer", "1.3.2")

	got, err := os.ReadFile(logPath)
	if err != nil {
		t.Fatalf("fake notify.sh was not invoked: %v", err)
	}
	out := string(got)
	if !strings.Contains(out, "--kind published") {
		t.Fatalf("invocation log %q missing --kind published", out)
	}
	if !strings.Contains(out, "--version 1.3.2") {
		t.Fatalf("invocation log %q missing --version 1.3.2", out)
	}
	if !strings.Contains(out, "CHANGELOG:"+notifyAdminURL) {
		t.Fatalf("invocation log %q missing changelog line with admin URL %q", out, notifyAdminURL)
	}
}

// TestNotifyPublishedSkipsMissingScript documents the dev-box/no-deploy-kit
// case: notifyPublished must return quietly, not panic or block, when the
// configured script simply isn't there.
func TestNotifyPublishedSkipsMissingScript(t *testing.T) {
	t.Setenv(notifyScriptEnv, filepath.Join(t.TempDir(), "does-not-exist.sh"))
	notifyPublished(context.Background(), "chillhub-installer", "1.3.2")
}

func shellQuote(s string) string {
	return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'"
}
