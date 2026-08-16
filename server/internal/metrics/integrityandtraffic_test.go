package metrics

import "testing"

// The launcher has always sent integrity_check, and the fold has always had no
// case for it: the event landed in Totals.Events and nowhere else, so a user
// verifying their files was invisible in the one panel built to notice that.
func TestSummaryCountsIntegrityChecks(t *testing.T) {
	h := New(t.TempDir())
	bodies := []string{
		`{"installId":"aaa","event":"integrity_check","gameId":"g1","version":"1.0.0","result":"ok","filesTotal":100}`,
		`{"installId":"bbb","event":"integrity_check","gameId":"g1","version":"1.0.0","result":"fail","filesTotal":100,"hashMismatches":3}`,
		`{"installId":"bbb","event":"integrity_check","gameId":"g2","version":"2.0.0","result":"fail","filesTotal":10,"hashMismatches":1}`,
	}
	for _, b := range bodies {
		if w := submit(t, h, b); w.Code != 200 {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}

	s := summary(t, h, "")
	if s.Totals.IntegrityChecks != 3 {
		t.Errorf("integrityChecks = %d, want 3", s.Totals.IntegrityChecks)
	}
	if s.Totals.IntegrityFailed != 2 {
		t.Errorf("integrityFailed = %d, want 2", s.Totals.IntegrityFailed)
	}
	if s.Totals.HashMismatches != 4 {
		t.Errorf("hashMismatches = %d, want 4", s.Totals.HashMismatches)
	}

	byGame := map[string]GameBucket{}
	for _, g := range s.ByGame {
		byGame[g.GameID] = g
	}
	if g := byGame["g1"]; g.IntegrityChecks != 2 || g.IntegrityFailed != 1 || g.HashMismatches != 3 {
		t.Errorf("g1 = %+v, want 2 checks / 1 failed / 3 mismatches", g)
	}
	if g := byGame["g2"]; g.IntegrityChecks != 1 || g.HashMismatches != 1 {
		t.Errorf("g2 = %+v, want 1 check / 1 mismatch", g)
	}

	// An integrity check is a one-off the user starts by hand; the daily table
	// tracks the volume of ordinary use and must not gain rows from it.
	for _, d := range s.ByDay {
		if d.Installs != 0 || d.Updates != 0 || d.LauncherStarts != 0 {
			t.Errorf("integrity check leaked into day bucket: %+v", d)
		}
	}
}

// An integrity_check without a gameId has no per-game bucket to fold into. The
// launcher never sends one, but /metrics/report is public and unauthenticated
// and an empty gameId is explicitly allowed there, so a stranger can post
// exactly this — and before the nil guard existed it would have been a panic in
// the admin's summary rather than a row nobody wanted.
func TestSummaryHandlesIntegrityCheckWithoutGame(t *testing.T) {
	h := New(t.TempDir())
	body := `{"installId":"aaa","event":"integrity_check","result":"fail","filesTotal":10,"hashMismatches":2}`
	if w := submit(t, h, body); w.Code != 200 {
		t.Fatalf("submit -> %d %s", w.Code, w.Body.String())
	}

	s := summary(t, h, "")
	if s.Totals.IntegrityChecks != 1 || s.Totals.IntegrityFailed != 1 {
		t.Errorf("totals = %d/%d, want 1/1", s.Totals.IntegrityChecks, s.Totals.IntegrityFailed)
	}
	if s.Totals.HashMismatches != 2 {
		t.Errorf("hashMismatches = %d, want 2", s.Totals.HashMismatches)
	}
	// No gameId means no per-game row: the event is still counted overall.
	if len(s.ByGame) != 0 {
		t.Errorf("byGame = %+v, want empty", s.ByGame)
	}
}

// Bytes alone cannot answer the question the launcher exists to answer. The
// store has carried fullBytes since the client learned to send it, and until now
// the summary threw it away — leaving "downloaded 40 MiB" with no "instead of
// 12 GiB" beside it.
func TestSummarySumsTrafficSavings(t *testing.T) {
	h := New(t.TempDir())
	bodies := []string{
		`{"installId":"aaa","event":"game_install","gameId":"g1","result":"ok","bytes":500,"filesDownloaded":5,"filesTotal":50,"fullBytes":5000}`,
		`{"installId":"aaa","event":"game_update","gameId":"g1","result":"ok","bytes":100,"filesDownloaded":2,"filesTotal":50,"fullBytes":5000}`,
		// integrity_check reports a filesTotal about a different operation
		// entirely: counting it here would quietly skew the ratio instead of
		// visibly missing it.
		`{"installId":"aaa","event":"integrity_check","gameId":"g1","result":"ok","filesTotal":50}`,
	}
	for _, b := range bodies {
		if w := submit(t, h, b); w.Code != 200 {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}

	s := summary(t, h, "")
	if s.Totals.BytesDownloaded != 600 {
		t.Errorf("bytesDownloaded = %d, want 600", s.Totals.BytesDownloaded)
	}
	if s.Totals.FullBytes != 10000 {
		t.Errorf("fullBytes = %d, want 10000", s.Totals.FullBytes)
	}
	if s.Totals.FilesDownloaded != 7 {
		t.Errorf("filesDownloaded = %d, want 7", s.Totals.FilesDownloaded)
	}
	if s.Totals.FilesTotal != 100 {
		t.Errorf("filesTotal = %d, want 100 (integrity check must not add its own)", s.Totals.FilesTotal)
	}
	if len(s.ByGame) != 1 || s.ByGame[0].FullBytes != 10000 {
		t.Errorf("byGame = %+v, want one row with fullBytes 10000", s.ByGame)
	}
}

// A cancelled operation is not a failed one: it must count as an attempt and
// stay out of both the failure share and the average duration.
func TestSummaryKeepsCancelOutOfFailures(t *testing.T) {
	h := New(t.TempDir())
	bodies := []string{
		`{"installId":"aaa","event":"game_install","gameId":"g1","result":"ok","durationMs":1000}`,
		`{"installId":"bbb","event":"game_install","gameId":"g1","result":"cancel","durationMs":9000}`,
	}
	for _, b := range bodies {
		if w := submit(t, h, b); w.Code != 200 {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}

	s := summary(t, h, "")
	if s.Totals.Installs != 2 {
		t.Errorf("installs = %d, want 2", s.Totals.Installs)
	}
	if s.Totals.InstallOK != 1 || s.Totals.InstallFail != 0 {
		t.Errorf("ok/fail = %d/%d, want 1/0", s.Totals.InstallOK, s.Totals.InstallFail)
	}
	if s.Totals.AvgInstallMs != 1000 {
		t.Errorf("avgInstallMs = %d, want 1000 (the cancelled 9s must not count)", s.Totals.AvgInstallMs)
	}
}
