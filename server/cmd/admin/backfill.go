package main

import (
	"flag"
	"fmt"
	"io"

	"ChillHub/server/internal/adminapi/builds"
)

// backfillCommand — подкоманда, дописывающая хеши блоков в уже опубликованные
// манифесты (см. builds.BackfillBlocks). Живёт в том же бинаре, что и админка:
// ей нужен тот же корень контента и тот же код манифестов, а отдельную
// программу пришлось бы отдельно и выкатывать.
//
// Запуск на сервере — от имени владельца контента, с тем же CONTENT_ROOT, что
// у службы:
//
//	chillhub-admin backfill-blocks [-game bodycam] [-version 0.8.12.19193]
const backfillCommand = "backfill-blocks"

// runBackfill разбирает аргументы, обходит версии и возвращает код выхода:
// 0 — всё дописано или дописывать нечего, 1 — хоть одна версия не удалась,
// 2 — неверные аргументы.
func runBackfill(h *builds.Handlers, args []string, out io.Writer) int {
	fs := flag.NewFlagSet(backfillCommand, flag.ContinueOnError)
	fs.SetOutput(out)
	game := fs.String("game", "", "only this game id")
	version := fs.String("version", "", "only this version")
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
		res, err := h.BackfillBlocks(t.NS, t.GameID, t.Version)
		switch {
		case err != nil:
			failed++
			_, _ = fmt.Fprintf(out, "%s: FAILED: %v\n", name, err)
		case res.Skipped != "":
			_, _ = fmt.Fprintf(out, "%s: skipped: %s\n", name, res.Skipped)
		default:
			_, _ = fmt.Fprintf(out, "%s: %d file(s), %.2f GB hashed\n", name, res.Files, float64(res.Bytes)/1e9)
		}
	}
	if failed > 0 {
		return 1
	}
	return 0
}
