package mods

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"strings"
	"time"
)

// The community endpoint redirects to a gzip blob listing chunk URLs. Each
// chunk contains the former v1 package array. A failed chunk must never leave
// a partial index in the cache: the resolver uses it to exclude foreign mods.
const (
	maxCommunityIndexBytes = 1 << 30
	maxCommunityChunkBytes = 64 << 20
	maxCommunityLinksBytes = 1 << 20
	maxCommunityChunks     = 2048
	communityIndexTTL      = time.Hour
	communityIndexTimeout  = 3 * time.Minute
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

	// packages is the same listing keyed by identity without the version:
	// "rob_gaming-driver". The resolver needs to tell "this game does not
	// serve this mod at all" apart from "this game does not serve THIS
	// version of it", and only the first is a reason to leave a dependency
	// out of the tree.
	packages map[string]bool
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

// Serves reports whether the community publishes the package a dependency
// string names, at any version.
//
// The version is always the last segment, so cutting it off is the one split
// of a dependency string that needs no guessing — which is what makes this
// answerable when Lookup, keyed by the exact version, says no.
func (i *CommunityIndex) Serves(dep string) bool {
	if i == nil {
		return false
	}
	d := strings.ToLower(strings.TrimSpace(dep))
	cut := strings.LastIndex(d, "-")
	if cut <= 0 {
		return false
	}
	return i.packages[d[:cut]]
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

// FetchCommunityIndex reads every chunk before publishing the new index.
// Limits apply after decompression, both per chunk and across the whole index.
func (c *Client) FetchCommunityIndex(ctx context.Context, community string) (*CommunityIndex, error) {
	if !safeCommunity(community) {
		return nil, fmt.Errorf("mods: unsafe community slug %q", community)
	}
	if idx := c.cachedIndex(community); idx != nil {
		return idx, nil
	}
	ctx, cancel := context.WithTimeout(ctx, communityIndexTimeout)
	defer cancel()
	endpoint := fmt.Sprintf("%s/c/%s/api/v1/package-listing-index/", c.apiBase, community)
	data, err := c.fetchIndexBlob(ctx, endpoint, maxCommunityLinksBytes)
	if err != nil {
		return nil, err
	}
	var links []string
	if err := json.Unmarshal(data, &links); err != nil {
		return nil, fmt.Errorf("mods: decode index links: %w", err)
	}
	if len(links) == 0 || len(links) > maxCommunityChunks {
		return nil, fmt.Errorf("mods: invalid index chunk count: %d", len(links))
	}
	// Validate the complete list before following even its first URL.
	seen := make(map[string]bool, len(links))
	for _, link := range links {
		if !c.allowedIndexURL(link) || seen[link] {
			return nil, fmt.Errorf("mods: invalid or duplicate index chunk URL %q", link)
		}
		seen[link] = true
	}
	idx := &CommunityIndex{community: community, versions: make(map[string]IndexedVersion, 1<<16), packages: make(map[string]bool, 1<<13)}
	remaining := int64(maxCommunityIndexBytes)
	for _, link := range links {
		data, err := c.fetchIndexBlob(ctx, link, min(int64(maxCommunityChunkBytes), remaining))
		if err != nil {
			return nil, err
		}
		remaining -= int64(len(data))
		if err := idx.readChunk(data); err != nil {
			return nil, fmt.Errorf("mods: community index %s: %w", community, err)
		}
	}
	log.Printf("[mods] индекс сообщества %s: %d частей, %d пакетов, %d версий", community, len(links), len(idx.packages), len(idx.versions))
	c.rememberIndex(idx)
	return idx, nil
}

func (i *CommunityIndex) readChunk(data []byte) error {
	dec := json.NewDecoder(bytes.NewReader(data))
	token, err := dec.Token()
	if err != nil {
		return err
	}
	if token != json.Delim('[') {
		return errors.New("mods: index chunk must be an array")
	}
	for dec.More() {
		var p communityPackage
		if err := dec.Decode(&p); err != nil {
			return err
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
			i.versions[strings.ToLower(v.FullName)] = IndexedVersion{Namespace: ns, Name: name, Version: v.VersionNum, FullName: v.FullName, Dependencies: v.Dependencies, DownloadURL: v.DownloadURL, FileSize: v.FileSize}
			i.packages[PackageKey(ns, name)] = true
		}
	}
	if _, err := dec.Token(); err != nil {
		return err
	}
	// Reading to EOF also rejects a truncated final bracket or trailing JSON.
	var extra any
	if err := dec.Decode(&extra); err != io.EOF {
		if err != nil {
			return fmt.Errorf("mods: trailing index data: %w", err)
		}
		return errors.New("mods: trailing index data")
	}
	return nil
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
