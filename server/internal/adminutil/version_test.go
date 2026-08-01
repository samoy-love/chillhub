package adminutil

import (
	"reflect"
	"testing"
)

func TestCompareVersions(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"1.1.10", "1.1.9", 1}, // the whole point: lexicographically this is "less"
		{"1.1.9", "1.1.10", -1},
		{"1.0.2", "1.1.3", -1},
		{"2.0.0", "10.0.0", -1},
		{"1.2", "1.2.0", 0},
		{"1.2.0", "1.2.0", 0},
		{"1.2.0-rc1", "1.2.0", -1},
		{"1.2.0-rc1", "1.2.0-rc2", -1},
		{"1.2.0", "1.2.0-rc1", 1},
	}
	for _, c := range cases {
		if got := CompareVersions(c.a, c.b); got != c.want {
			t.Errorf("CompareVersions(%q, %q) = %d, want %d", c.a, c.b, got, c.want)
		}
	}
}

func TestSortVersionsDescAndMax(t *testing.T) {
	in := []string{"1.0.2", "1.1.3", "1.1.7", "1.1.8", "1.1.9", "1.1.10"}
	want := []string{"1.1.10", "1.1.9", "1.1.8", "1.1.7", "1.1.3", "1.0.2"}
	got := append([]string(nil), in...)
	SortVersionsDesc(got)
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("SortVersionsDesc = %v, want %v", got, want)
	}
	if m := MaxVersion(in); m != "1.1.10" {
		t.Fatalf("MaxVersion = %q, want 1.1.10", m)
	}
	if m := MaxVersion(nil); m != "" {
		t.Fatalf("MaxVersion(nil) = %q, want empty", m)
	}
}
