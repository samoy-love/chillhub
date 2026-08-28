package mods

import (
	"context"
	"os"
	"testing"
	"time"
)

// Живая проверка выигрыша от индекса сообщества. Гоняется вручную:
//
//	CHILLHUB_NET_TESTS=1 go test ./internal/adminapi/mods/ -run TestLiveIndexSpeedsUpResolve -v
//
// В обычном прогоне пропускается: ходит в настоящий Thunderstore и качает
// 34 МБ.
func TestLiveIndexSpeedsUpResolve(t *testing.T) {
	if os.Getenv("CHILLHUB_NET_TESTS") != "1" {
		t.Skip("сетевой тест; включается CHILLHUB_NET_TESTS=1")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()

	c := NewClient(nil)
	eco := &Ecosystem{}
	const root = "ASTeam-LethalReloaded-2.2.12"

	t0 := time.Now()
	idx, err := c.FetchCommunityIndex(ctx, "lethal-company")
	if err != nil {
		t.Fatalf("индекс сообщества: %v", err)
	}
	tIndex := time.Since(t0)
	t.Logf("индекс: %d версий за %v", idx.Len(), tIndex)

	t1 := time.Now()
	withIdx, err := c.ResolveListWithIndex(ctx, eco, []string{root}, nil, idx)
	if err != nil {
		t.Fatalf("разбор с индексом: %v", err)
	}
	tWith := time.Since(t1)

	t.Logf("с индексом: %d пакетов, %d недоступно, %v (вместе с индексом %v)",
		len(withIdx.Packages), len(withIdx.Missing), tWith, tIndex+tWith)

	sized := 0
	for _, p := range withIdx.Packages {
		if v, ok := idx.Lookup(p.FullName); ok && v.FileSize > 0 {
			sized++
		}
	}
	t.Logf("размеры архивов известны без единого запроса: %d из %d", sized, len(withIdx.Packages))

	if len(withIdx.Missing) > 0 {
		t.Errorf("пакеты объявлены исчезнувшими: %v", withIdx.Missing)
	}
}
