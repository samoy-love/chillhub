package builds

import (
	"bytes"
	"errors"
	"log"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// captureLog redirects the standard logger for the duration of one test and
// returns the accumulated output. The rollback path has no return value and no
// HTTP response: the journal is the ONLY signal an operator gets that a
// published version is sitting on disk under a backup name, so the message is
// part of the contract and is asserted on.
func captureLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	prevOut := log.Writer()
	prevFlags := log.Flags()
	log.SetOutput(&buf)
	log.SetFlags(0)
	t.Cleanup(func() {
		log.SetOutput(prevOut)
		log.SetFlags(prevFlags)
	})
	return &buf
}

// Nothing was published yet is the common case — the first build of a game, or
// of the launcher itself. A rollback then has nothing to restore and must stay
// silent instead of touching the destination or crying wolf in the journal.
func TestRollbackLiveVersionIsANoOpWithoutABackup(t *testing.T) {
	logs := captureLog(t)
	final := filepath.Join(t.TempDir(), "content", "game", "1.0.0")

	rollbackLiveVersion("", final, errors.New("promote failed"))

	if _, err := os.Stat(final); err == nil {
		t.Fatal("rollback created the destination out of nothing")
	}
	if logs.Len() != 0 {
		t.Fatalf("a first publish logged a rollback failure: %s", logs.String())
	}
}

// The happy rollback: the previous build goes back under its published name, so
// clients that were downloading it keep downloading it.
func TestRollbackLiveVersionRestoresThePreviousBuild(t *testing.T) {
	logs := captureLog(t)
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	backup := final + ".old-abcdef123456"
	mustMkdirAll(t, backup)
	mustWriteFile(t, filepath.Join(backup, "live.txt"), "live")

	rollbackLiveVersion(backup, final, errors.New("promote failed"))

	b, err := os.ReadFile(filepath.Join(final, "live.txt")) // #nosec G304 -- built from t.TempDir().
	if err != nil {
		t.Fatalf("the previous build was not restored: %v", err)
	}
	if string(b) != "live" {
		t.Fatalf("restored content = %q", string(b))
	}
	assertNoBackupDirs(t, filepath.Dir(final))
	if logs.Len() != 0 {
		t.Fatalf("a successful rollback logged a failure: %s", logs.String())
	}
}

// Both the promote and the rollback failing is the one state nobody can fix
// from the panel: the published version exists only under a random ".old-…"
// name. The message must name that path and stand out, because it is the only
// thing standing between an operator and a build restored by guesswork.
func TestRollbackLiveVersionShoutsWhenTheRestoreFails(t *testing.T) {
	logs := captureLog(t)
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	// A backup path that does not exist makes the restoring rename fail.
	backup := final + ".old-deadbeefcafe"

	rollbackLiveVersion(backup, final, errors.New("promote failed"))

	out := logs.String()
	if !strings.Contains(out, "CRITICAL") {
		t.Fatalf("an unrecoverable state was logged without CRITICAL: %q", out)
	}
	if !strings.Contains(out, backup) {
		t.Fatalf("the journal does not name the backup path to recover from: %q", out)
	}
	if !strings.Contains(out, "promote failed") {
		t.Fatalf("the original promote error was dropped: %q", out)
	}
}

// promoteVersionDir must not delete the previous build when it could neither
// promote the new one nor put the old one back. The backup is the last copy;
// dropping it would turn a recoverable incident into a lost release.
func TestPromoteVersionDirKeepsTheBackupWhenTheRestoreFails(t *testing.T) {
	logs := captureLog(t)
	root := t.TempDir()
	parent := filepath.Join(root, "content", "game")
	final := filepath.Join(parent, "1.0.0")
	mustMkdirAll(t, final)
	mustWriteFile(t, filepath.Join(final, "live.txt"), "live")

	// The staging directory does not exist, so the promoting rename fails; the
	// obstruction then makes the restoring rename fail too.
	stage := filepath.Join(parent, "1.0.0.tmp-missing")
	if !denyWritesIn(t, parent) {
		t.Skip("the filesystem does not honour a read-only parent directory here")
	}
	if err := promoteVersionDir(stage, final); err == nil {
		t.Fatal("promoting a missing staging dir reported success")
	}
	if !strings.Contains(logs.String(), "CRITICAL") {
		t.Fatalf("the unrecoverable state was not reported: %q", logs.String())
	}
	matches, _ := filepath.Glob(final + ".old-*")
	if len(matches) != 1 {
		t.Fatalf("expected exactly one surviving backup, got %v", matches)
	}
	b, err := os.ReadFile(filepath.Join(matches[0], "live.txt")) // #nosec G304 -- built from t.TempDir().
	if err != nil || string(b) != "live" {
		t.Fatalf("the last copy of the published build is damaged: %q (%v)", string(b), err)
	}
}

// A version that cannot be moved aside must abort the publication BEFORE the
// staging tree is renamed into place. Otherwise the promote would happen with
// the old directory still there, and the failure would surface as a corrupted
// live version instead of a refused publish.
func TestPromoteVersionDirAbortsWhenTheLiveVersionCannotBeMovedAside(t *testing.T) {
	root := t.TempDir()
	parent := filepath.Join(root, "content", "game")
	final := filepath.Join(parent, "1.0.0")
	mustMkdirAll(t, final)
	mustWriteFile(t, filepath.Join(final, "live.txt"), "live")
	stage := filepath.Join(parent, "1.0.0.tmp-staged")
	mustMkdirAll(t, stage)
	mustWriteFile(t, filepath.Join(stage, "new.txt"), "new")

	if !denyRenamesOf(t, final) {
		t.Skip("the filesystem does not let a directory rename be blocked here")
	}
	if err := promoteVersionDir(stage, final); err == nil {
		t.Fatal("promote reported success although the live version could not be moved aside")
	}
	b, err := os.ReadFile(filepath.Join(final, "live.txt")) // #nosec G304 -- built from t.TempDir().
	if err != nil || string(b) != "live" {
		t.Fatalf("the published build was disturbed: %q (%v)", string(b), err)
	}
	if _, err := os.Stat(filepath.Join(stage, "new.txt")); err != nil {
		t.Fatalf("the staging tree was consumed by a failed promote: %v", err)
	}
	assertNoBackupDirs(t, parent)
}

// denyWritesIn makes new entries inside dir — created or renamed into place —
// fail, and reports whether it worked. Windows and root ignore the permission
// bits, so the caller skips instead of asserting on behaviour the platform will
// not produce.
func denyWritesIn(t *testing.T, dir string) bool {
	t.Helper()
	if runtime.GOOS == "windows" {
		return false
	}
	restoreMode(t, dir, 0o500)
	probe := filepath.Join(dir, "rename-probe")
	if err := os.Mkdir(probe, 0o750); err == nil {
		_ = os.RemoveAll(probe)
		return false
	}
	return true
}

// denyRenamesOf blocks renaming dir itself. On Windows an open handle to a file
// inside it is enough; elsewhere the parent has to be made read-only. The
// obstruction is probed rather than assumed.
func denyRenamesOf(t *testing.T, dir string) bool {
	t.Helper()
	if runtime.GOOS == "windows" {
		held := filepath.Join(dir, "held.bin")
		mustWriteFile(t, held, "held")
		f, err := os.Open(held) // #nosec G304 -- built from t.TempDir().
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { _ = f.Close() })
	} else {
		restoreMode(t, filepath.Dir(dir), 0o500)
	}
	probe := dir + ".rename-probe"
	if err := os.Rename(dir, probe); err != nil {
		return true
	}
	if err := os.Rename(probe, dir); err != nil {
		t.Fatalf("the probe rename could not be undone: %v", err)
	}
	return false
}

// restoreMode chmods dir and puts the original bits back when the test ends.
func restoreMode(t *testing.T, dir string, mode os.FileMode) {
	t.Helper()
	st, err := os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(dir, mode); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(dir, st.Mode().Perm()) })
}
