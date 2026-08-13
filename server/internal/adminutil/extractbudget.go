package adminutil

import (
	"errors"
	"fmt"
	"io"
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
type ExtractBudget struct {
	limit     int64
	remaining int64
}

// NewExtractBudget returns a budget capped at limit bytes.
func NewExtractBudget(limit int64) *ExtractBudget {
	return &ExtractBudget{limit: limit, remaining: limit}
}

// Copy writes src into dst, counting the bytes actually produced against the
// shared budget and failing as soon as it's exhausted.
func (b *ExtractBudget) Copy(dst io.Writer, src io.Reader) error {
	n, err := io.Copy(dst, io.LimitReader(src, b.remaining+1))
	b.remaining -= n
	if err != nil {
		return err
	}
	if b.remaining < 0 {
		return fmt.Errorf("%w (%d bytes)", ErrExtractBudgetExceeded, b.limit)
	}
	return nil
}
