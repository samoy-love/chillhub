package main

import (
	"flag"
	"fmt"
	"io"

	"ChillHub/server/internal/adminapi/builds"
)

// backfillCommand — подкоманда, дополняющая уже опубликованные манифесты.
// Живёт в том же бинаре, что и админка:
// ей нужен тот же корень контента и тот же код манифестов, а отдельную
// программу пришлось бы отдельно и выкатывать.
//
// Запуск на сервере — от имени владельца контента, с тем же CONTENT_ROOT, что
// у службы:
//
//	chillhub-admin backfill-blocks [--small-packs] [-game bodycam] [-version 0.8.12.19193]
const backfillCommand = "backfill-blocks"

// runBackfill разбирает аргументы, обходит версии и возвращает код выхода:
// 0 — всё дописано или дописывать нечего, 1 — хоть одна версия не удалась,
// 2 — неверные аргументы.
func runBackfill(h *builds.Handlers, args []string, out io.Writer) int {
	fs := flag.NewFlagSet(backfillCommand, flag.ContinueOnError)
	fs.SetOutput(out)
	game := fs.String("game", "", "only this game id")
	version := fs.String("version", "", "only this version")
	smallPacks := fs.Bool("small-packs", false, "backfill small-file packs instead of block hashes")
	if err := fs.Parse(args); err != nil {
		return 2
	}

	targets, err := h.BackfillTargets(*game, *version)
	if err != nil {
		_, _ = fmt.Fprintf(out, "list versions: %v\n", err)
		return 1
	}
	if len(targets) == 0 {
		_, _ = fmt.Fprintln(out, "no published versions match")
		return 1
	}

	failed := 0
	for _, t := range targets {
		name := t.GameID + "/" + t.Version
		if t.NS != builds.NamespaceGame {
			name = string(t.NS) + "/" + name
		}
		message, err := backfillVersion(h, t, *smallPacks)
		if err != nil {
			failed++
			_, _ = fmt.Fprintf(out, "%s: FAILED: %v\n", name, err)
		} else {
			_, _ = fmt.Fprintf(out, "%s: %s\n", name, message)
		}
	}
	if failed > 0 {
		return 1
	}
	return 0
}

func backfillVersion(h *builds.Handlers, target builds.BackfillTarget, smallPacks bool) (string, error) {
	if smallPacks {
		res, err := h.BackfillSmallPacks(target.NS, target.GameID, target.Version)
		if err != nil {
			return "", err
		}
		if res.Packs == 0 && !res.ManifestUpdated {
			return "skipped: no eligible small-file packs", nil
		}
		status := "manifest unchanged"
		if res.ManifestUpdated {
			status = "manifest updated"
		}
		return fmt.Sprintf("%d small pack(s), %d file(s), %s", res.Packs, res.Files, status), nil
	}
	res, err := h.BackfillBlocks(target.NS, target.GameID, target.Version)
	if err != nil {
		return "", err
	}
	if res.Skipped != "" {
		return "skipped: " + res.Skipped, nil
	}
	return fmt.Sprintf("%d file(s), %.2f GB hashed", res.Files, float64(res.Bytes)/1e9), nil
}
