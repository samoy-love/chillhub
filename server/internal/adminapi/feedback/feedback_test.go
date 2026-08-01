package feedback

import (
	"testing"

	"ChillHub/server/internal/adminutil"
)

// Feedback rotation must bound the number of stored reports.
func TestPruneFeedbackItemsRotatesOldest(t *testing.T) {
	items := make([]Item, MaxItems+50)
	for i := range items {
		items[i] = Item{ID: adminutil.GenID(), Comment: "c"}
	}
	items[0].ID = "newest"
	out := Prune(items)
	if len(out) != MaxItems {
		t.Fatalf("expected %d items, got %d", MaxItems, len(out))
	}
	if out[0].ID != "newest" {
		t.Fatal("newest report was dropped")
	}
}
