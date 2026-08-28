package mods

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"strings"
	"time"
)

// ОДИН ЗАПРОС ВМЕСТО ПОЛУТОРА СОТЕН.
//
// Разбор состава модпака — это обход дерева зависимостей, и до сих пор каждый
// его узел стоил отдельного запроса к API. Клиент держит себя примерно на трёх
// запросах в секунду (иначе Thunderstore отвечает 429), поэтому LethalReloaded
// со своими 149 зависимостями разбирался около пятидесяти секунд, а следом ещё
// столько же уходило на опрос размеров архивов.
//
// У Thunderstore есть адрес, отдающий ВСЕ пакеты сообщества разом:
//
//	https://thunderstore.io/c/{slug}/api/v1/package/
//
// Для lethal-company это 34.5 МБ, 50 568 пакетов, 191 915 версий — и приходит
// оно за секунду. В каждой версии лежит ровно то, ради чего делались все те
// запросы: список зависимостей, адрес архива и его размер. Проверено на живом
// модпаке: 149 прямых зависимостей из 149 нашлись в этом ответе.
//
// ЭТО ЖЕ ЧИНИТ И РАЗБОР ИМЁН. В строке «swuff-star-ConfigurableCrafting-1.0.0»
// нельзя угадать, где кончается пространство имён и начинается имя мода:
// дефисы есть и там, и там. Индекс избавляет от догадки вовсе — он найден по
// полному имени версии, а пространство имён и имя лежат в нём отдельными
// полями.
//
// Индекс НЕ обязателен: чего в нём нет (зависимость из другого сообщества),
// то по-прежнему спрашивается поштучно. Поэтому его неудача — не отказ сборки,
// а возврат к прежней скорости.

const (
	// maxCommunityIndexBytes bounds the listing AFTER decompression.
	//
	// Мера, а не догадка: lethal-company приезжает по сети как 34.5 МБ gzip и
	// разворачивается в 327 МБ JSON. Прежний потолок в 256 МБ резал поток
	// посередине, и разбор падал с «unexpected EOF» — то есть индекс не работал
	// вовсе на самом большом сообществе, ради которого и затевался.
	//
	// Гигабайт оставлен не «на всякий случай»: он останавливает бесконечный
	// поток, а не большой ответ. Само тело при этом не держится в памяти —
	// декодер идёт по нему потоком.
	maxCommunityIndexBytes = 1 << 30

	// communityIndexTTL is how long a built index is reused.
	//
	// Версии на Thunderstore неизменны, меняется только состав: между сборками
	// список может пополниться, и через час его стоит перечитать. Внутри одной
	// сессии оператора — «собрать, посмотреть дифф, собрать соседний пак» —
	// перечитывать 327 МБ на каждую сборку незачем.
	communityIndexTTL = time.Hour

	// communityIndexTimeout bounds the whole download. It is one request, but a
	// big one, and the default request timeout is sized for small documents.
	communityIndexTimeout = 3 * time.Minute
)

// IndexedVersion is what the community listing knows about one published
// version — everything the resolver used to ask for package by package.
type IndexedVersion struct {
	Namespace    string
	Name         string
	Version      string
	FullName     string
	Dependencies []string
	DownloadURL  string
	FileSize     int64
}

// CommunityIndex is every version a community serves, keyed by full name.
//
// Read-only once built, so the resolver's goroutines share one without a lock.
type CommunityIndex struct {
	community string
	versions  map[string]IndexedVersion
}

// Community is the slug the index was built for.
func (i *CommunityIndex) Community() string {
	if i == nil {
		return ""
	}
	return i.community
}

// Len is how many versions the index holds.
func (i *CommunityIndex) Len() int {
	if i == nil {
		return 0
	}
	return len(i.versions)
}

// Lookup finds one version by its dependency string ("Author-Mod-1.2.3").
func (i *CommunityIndex) Lookup(dep string) (IndexedVersion, bool) {
	if i == nil {
		return IndexedVersion{}, false
	}
	v, ok := i.versions[strings.ToLower(strings.TrimSpace(dep))]
	return v, ok
}

// AsPackageVersion converts an index entry into the document the resolver
// expects, so the walk cannot tell where the answer came from.
func (v IndexedVersion) AsPackageVersion() *PackageVersion {
	return &PackageVersion{
		Namespace:     v.Namespace,
		Name:          v.Name,
		VersionNumber: v.Version,
		FullName:      v.FullName,
		Dependencies:  v.Dependencies,
		DownloadURL:   v.DownloadURL,
		FileSize:      v.FileSize,
		IsActive:      true,
	}
}

// communityPackage is one entry of the v1 listing, cut down to what is used.
// The full document carries descriptions, icons and rating counts for fifty
// thousand packages; decoding them would cost more memory than the whole point
// of this file saves in time.
type communityPackage struct {
	Owner    string `json:"owner"`
	Name     string `json:"name"`
	Versions []struct {
		Namespace    string   `json:"namespace"`
		Name         string   `json:"name"`
		FullName     string   `json:"full_name"`
		VersionNum   string   `json:"version_number"`
		Dependencies []string `json:"dependencies"`
		DownloadURL  string   `json:"download_url"`
		FileSize     int64    `json:"file_size"`
	} `json:"versions"`
}

// FetchCommunityIndex downloads a community's whole package listing.
//
// The body is decoded as a stream rather than read into memory first: at 34 MB
// the difference between "one buffer" and "one buffer plus the parsed result"
// is real, and there is no reason to hold the raw bytes at all.
func (c *Client) FetchCommunityIndex(ctx context.Context, community string) (*CommunityIndex, error) {
	if !safeCommunity(community) {
		return nil, fmt.Errorf("mods: unsafe community slug %q", community)
	}
	if idx := c.cachedIndex(community); idx != nil {
		return idx, nil
	}

	release, err := c.acquire(ctx)
	if err != nil {
		return nil, err
	}
	defer release()

	reqCtx, cancel := context.WithTimeout(ctx, communityIndexTimeout)
	defer cancel()

	url := fmt.Sprintf("%s/c/%s/api/v1/package/", c.apiBase, community)
	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", userAgent)
	req.Header.Set("Accept", "application/json")

	res, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("mods: community index %s: unexpected status %d", community, res.StatusCode)
	}

	idx := &CommunityIndex{community: community, versions: make(map[string]IndexedVersion, 1<<16)}
	dec := json.NewDecoder(io.LimitReader(res.Body, maxCommunityIndexBytes))

	// Открывающая скобка массива читается отдельно, дальше пакеты идут по
	// одному: держать в памяти сразу пятьдесят тысяч разобранных пакетов
	// незачем, нужны только их версии.
	if _, err := dec.Token(); err != nil {
		return nil, fmt.Errorf("mods: community index %s: %w", community, err)
	}
	for dec.More() {
		var p communityPackage
		if err := dec.Decode(&p); err != nil {
			return nil, fmt.Errorf("mods: community index %s: %w", community, err)
		}
		for _, v := range p.Versions {
			if v.FullName == "" {
				continue
			}
			ns := v.Namespace
			if ns == "" {
				ns = p.Owner
			}
			name := v.Name
			if name == "" {
				name = p.Name
			}
			idx.versions[strings.ToLower(v.FullName)] = IndexedVersion{
				Namespace:    ns,
				Name:         name,
				Version:      v.VersionNum,
				FullName:     v.FullName,
				Dependencies: v.Dependencies,
				DownloadURL:  v.DownloadURL,
				FileSize:     v.FileSize,
			}
		}
	}

	log.Printf("[mods] индекс сообщества %s: %d версий", community, len(idx.versions))
	c.rememberIndex(idx)
	return idx, nil
}

// cachedIndex returns a still-fresh index for the community, or nil.
func (c *Client) cachedIndex(community string) *CommunityIndex {
	c.idxMu.Lock()
	defer c.idxMu.Unlock()
	if c.idx == nil || c.idx.community != community {
		return nil
	}
	if time.Since(c.idxAt) > communityIndexTTL {
		return nil
	}
	return c.idx
}

// rememberIndex keeps the last built index. ONE, not a map per community: the
// thing weighs tens of megabytes, and a server that builds packs for three
// games would otherwise hold three of them for an hour each.
func (c *Client) rememberIndex(idx *CommunityIndex) {
	c.idxMu.Lock()
	defer c.idxMu.Unlock()
	c.idx = idx
	c.idxAt = time.Now()
}
