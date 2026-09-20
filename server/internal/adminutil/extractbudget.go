package adminutil

import (
	"errors"
	"fmt"
	"io"
	"sync/atomic"
)

// ErrExtractBudgetExceeded is returned by ExtractBudget.Copy once the shared
// byte budget is exhausted. Callers typically wrap it with %w to add their own
// context (which archive, which package) while keeping errors.Is(err,
// ErrExtractBudgetExceeded) working for tests and callers that just need to
// know "too big", not the exact wording.
var ErrExtractBudgetExceeded = errors.New("archive expands beyond the allowed size")

// ExtractBudget bounds the total number of bytes one or more zip extractions
// sharing this instance may write. The zip header's declared uncompressed
// size is never trusted for this — the sizes in a ZIP header can neither be
// trusted for a precheck nor relied upon during extraction: a small archive
// can declare tiny entries and then stream far more than that, so the real,
// written byte count is counted here instead and the copy is cut off as soon
// as the budget is gone.
//
// A budget is shared across every entry of one archive (so a small compressed
// file with many entries can't add up to more than the limit one entry at a
// time), and can equally be shared across MULTIPLE archives/extractions when
// the caller wants a cumulative cap over a whole batch — construct one
// instance with NewExtractBudget and pass the SAME instance to every Copy
// call that should count against the same limit.
// СЧЁТЧИК АТОМАРНЫЙ, ПОТОМУ ЧТО РАСПАКОВКА ИДЁТ В НЕСКОЛЬКО ПОТОКОВ.
// Обычное поле здесь — гонка на защите от zip-бомбы: каждый поток читал бы
// остаток до вычитаний соседей, и вместе они выписали бы на диск больше
// лимита, каждый «в пределах».
type ExtractBudget struct {
	limit     int64
	remaining atomic.Int64
}

// NewExtractBudget returns a budget capped at limit bytes.
func NewExtractBudget(limit int64) *ExtractBudget {
	b := &ExtractBudget{limit: limit}
	b.remaining.Store(limit)
	return b
}

// Copy writes src into dst, counting the bytes actually produced against the
// shared budget and failing as soon as it's exhausted.
//
// Байты списываются ПО ХОДУ записи, а не в конце: раньше проверка стояла
// после копирования целиком, и запись обрывалась только потому, что
// LimitReader не давал прочитать больше остатка. С несколькими потоками
// такого предела на каждого не хватает — считать надо там же, где пишется.
func (b *ExtractBudget) Copy(dst io.Writer, src io.Reader) error {
	cw := &budgetWriter{dst: dst, budget: b}
	_, err := io.Copy(cw, src)
	if cw.exceeded {
		return fmt.Errorf("%w (%d bytes)", ErrExtractBudgetExceeded, b.limit)
	}
	return err
}

// budgetWriter списывает записанное с общего остатка и обрывает копирование,
// как только тот ушёл в минус.
type budgetWriter struct {
	dst      io.Writer
	budget   *ExtractBudget
	exceeded bool
}

func (w *budgetWriter) Write(p []byte) (int, error) {
	if w.budget.remaining.Add(-int64(len(p))) < 0 {
		w.exceeded = true
		return 0, ErrExtractBudgetExceeded
	}
	n, err := w.dst.Write(p)
	if n < len(p) {
		// Недописанное возвращаем в общий остаток: списано было всё.
		w.budget.remaining.Add(int64(len(p) - n))
	}
	return n, err
}
