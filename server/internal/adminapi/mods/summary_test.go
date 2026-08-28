package mods

import (
	"testing"
)

// СРАВНЕНИЕ ВЕРСИЙ ПО ЧИСЛАМ, А НЕ ПО СТРОКАМ.
//
// Строковое «1.6.10» меньше «1.6.9», и значок «есть версия свежее» загорался бы
// ровно наоборот: панель звала бы активировать старую сборку и молчала бы про
// новую.
func TestNewestVersionComparesNumbers(t *testing.T) {
	cases := []struct {
		what string
		in   []string
		want string
	}{
		{"двузначная доля старше однозначной", []string{"1.6.9", "1.6.10", "1.6.3"}, "1.6.10"},
		{"разная длина", []string{"1.6", "1.6.1"}, "1.6.1"},
		{"старшая часть решает", []string{"1.9.9", "2.0.0"}, "2.0.0"},
		{"один элемент", []string{"1.0.0"}, "1.0.0"},
		{"пусто", nil, ""},
	}
	for _, c := range cases {
		t.Run(c.what, func(t *testing.T) {
			if got := newestVersion(c.in); got != c.want {
				t.Errorf("newestVersion(%v) = %q, want %q", c.in, got, c.want)
			}
		})
	}
}

// Версия, которая не разбирается на числа, не должна ни падать, ни вытеснять
// нормальную: лаунчер такие не публикует, но реестр — файл на диске.
func TestNewestVersionSurvivesGarbage(t *testing.T) {
	if got := newestVersion([]string{"1.6.5", "не-версия"}); got != "1.6.5" {
		t.Errorf("мусор победил настоящую версию: %q", got)
	}
	if got := newestVersion([]string{"", ""}); got != "" {
		t.Errorf("из пустых строк выбрано %q", got)
	}
}

// Значок лаунчера обязан молчать, пока решать нечего: пустая история публикаций
// и совпадение активной с самой свежей — оба не повод для внимания.
func TestLauncherSummaryStaysQuietWithNothingToDecide(t *testing.T) {
	fs := newFakeStore(t)
	b, root := testBuilder(t, fs)
	h := New(root, b.Builds, nil)

	got := h.launcherSummary()

	if got.Pending {
		t.Errorf("значок горит на пустом реестре: %+v", got)
	}
}
