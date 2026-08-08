package builds

import (
	"context"
	"log"
	"os"
	"os/exec"
	"strings"
	"time"
)

// notifyScriptEnv overrides the default path to deploy-kit's lib/notify.sh.
// The server doesn't control where deploy-kit is installed on the host; the
// default matches the layout every other server-side deploy-kit script here
// already assumes (release.sh, rollback.sh, publish-file.sh all live under
// /opt/deploy-kit — see the CI log that runs "sudo /opt/deploy-kit/publish-file.sh").
const notifyScriptEnv = "DEPLOY_KIT_NOTIFY_SCRIPT"

const defaultNotifyScript = "/opt/deploy-kit/lib/notify.sh"

// notifyAdminURL is where the "published" event points a reader who wants to
// see or manage the version that just went live.
const notifyAdminURL = "https://launcher.samoy.love/admin/#launcher"

// notifyPublishedArgs builds the argument list for lib/notify.sh --mode local.
//
// kind=published, not success: success only means "the build finished" — for
// the launcher that's the least interesting half of the story. The build sits
// on the server, inert, until a human clicks Activate here. published is the
// contract's word for "now live for users" (docs/events.md in deploy-kit),
// and that is exactly what Activate just did.
//
// --mode local (not ssh): this call runs ON the same host lib/notify.sh
// writes its event files to, so there is no network hop to make — see
// --mode local in lib/notify.sh, the same path release.sh/rollback.sh/
// publish-file.sh already use to raise events from the machine itself rather
// than over SSH from CI.
func notifyPublishedArgs(app, version string, changelogFile string) []string {
	args := []string{
		"--mode", "local",
		"--source", "local",
		"--kind", "published",
		"--app", app,
		"--version", version,
	}
	if changelogFile != "" {
		args = append(args, "--changelog-file", changelogFile)
	}
	return args
}

// notifyPublished tells deploy-kit's event log that a version went live, on
// the same channel every other release uses. Best-effort by design: a failed
// notification must never fail — or even taint — the activation it reports
// on. The version is already live; whether the operator hears about it is a
// separate concern from whether it went live.
//
// The changelog field carries a plain-text deep link to the admin panel's
// Launcher tab (see admin.js's hash routing) rather than commitURL/runURL:
// those two are validated against https://github.com/… only (lib/notify.sh),
// and an admin.samoy.love link would just be silently dropped there.
func notifyPublished(app, version string) {
	script := strings.TrimSpace(os.Getenv(notifyScriptEnv))
	if script == "" {
		script = defaultNotifyScript
	}
	if _, err := os.Stat(script); err != nil {
		// Not an error on a dev box or a host without deploy-kit installed:
		// the admin service still has a job to do without it.
		return
	}

	changelogFile, cleanup, err := writeChangelogFile(notifyAdminURL)
	if err != nil {
		log.Printf("[builds] notify published %s %s: changelog temp file: %v", app, version, err)
		changelogFile = ""
	} else {
		defer cleanup()
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	args := append([]string{script}, notifyPublishedArgs(app, version, changelogFile)...)
	out, err := exec.CommandContext(ctx, "bash", args...).CombinedOutput()
	if err != nil {
		log.Printf("[builds] notify published %s %s: %v: %s", app, version, err, strings.TrimSpace(string(out)))
		return
	}
	log.Printf("[builds] notify published %s %s: %s", app, version, strings.TrimSpace(string(out)))
}

// writeChangelogFile stages the one-line changelog lib/notify.sh reads via
// --changelog-file. A real file, not a pipe or an argument: the script reads
// it with a plain shell redirect, and a temp file is the simplest thing that
// works identically to the CHANGELOG_FILE convention the release pipeline
// already uses elsewhere.
func writeChangelogFile(line string) (path string, cleanup func(), err error) {
	f, err := os.CreateTemp("", "chillhub-notify-changelog-*.txt")
	if err != nil {
		return "", nil, err
	}
	name := f.Name()
	if _, err := f.WriteString(line + "\n"); err != nil {
		_ = f.Close()
		_ = os.Remove(name)
		return "", nil, err
	}
	if err := f.Close(); err != nil {
		_ = os.Remove(name)
		return "", nil, err
	}
	return name, func() { _ = os.Remove(name) }, nil
}
