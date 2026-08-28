package mods

import (
	"context"
	"testing"
)

// TestBuildResolvesThroughCommunityIndex: сборка берёт состав из списка
// сообщества, а не спрашивает пакеты по одному.
//
// Это и есть весь выигрыш: поштучный обход ограничен тремя запросами в секунду,
// и на живом модпаке из 151 пакета он занимал около минуты против двух секунд
// на один список.
func TestBuildResolvesThroughCommunityIndex(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatal(err)
	}

	if fs.apiHits["community-index"] != 1 {
		t.Errorf("список сообщества запрошен %d раз вместо одного", fs.apiHits["community-index"])
	}
	for full := range fs.deps {
		if n := fs.apiHits[full]; n > 0 {
			t.Errorf("пакет %s всё равно спрошен поштучно (%d раз)", full, n)
		}
	}
}

// TestBuildSurvivesUnavailableIndex: список сообщества — ускорение, а не
// условие работы. Его недоступность обязана стоить времени, а не сборки.
func TestBuildSurvivesUnavailableIndex(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.disableIndex()
	b, _ := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatalf("сборка не пережила недоступный список сообщества: %v", err)
	}

	// Без индекса пакеты снова спрашиваются по одному — иначе состав взялся бы
	// из ниоткуда.
	asked := 0
	for full := range fs.deps {
		asked += fs.apiHits[full]
	}
	if asked == 0 {
		t.Error("без индекса не было ни одного запроса метаданных")
	}
}

// TestCommunityIndexIsReusedBetweenBuilds: список весит сотни мегабайт в
// разобранном виде, и качать его на каждую сборку соседнего пака незачем.
func TestCommunityIndexIsReusedBetweenBuilds(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	for range 2 {
		if _, err := b.Resolve(context.Background(), thunderstoreRequest()); err != nil {
			t.Fatal(err)
		}
	}

	if fs.apiHits["community-index"] != 1 {
		t.Errorf("список сообщества скачан %d раз — кеш не сработал", fs.apiHits["community-index"])
	}
}
