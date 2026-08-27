package mods

import (
	"context"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Browsing Thunderstore from the admin panel.
//
// The catalogue is fetched by the SERVER, never by the browser: the panel's
// fetch wrapper rewrites paths and attaches a CSRF token, and pointing it at a
// third-party host would trip CORS and leak the panel's requests to that host
// at the same time.
//
// One detail decides whether the filter works at all: the "Modpacks" section is
// addressed by a UUID that is DIFFERENT FOR EVERY GAME (lethal-company and
// how-to-fish have unrelated ids), and passing the slug instead — the obvious
// guess — is accepted and silently ignored, returning the whole catalogue. So
// the id is looked up per community and cached.

// Section is one tab of a community's package list.
type Section struct {
	UUID     string `json:"uuid"`
	Name     string `json:"name"`
	Slug     string `json:"slug"`
	Priority int    `json:"priority"`
}

// filtersDoc is the shape of /api/cyberstorm/community/{slug}/filters/.
type filtersDoc struct {
	Sections []Section `json:"sections"`
}

// CatalogEntry is one package as the listing endpoint reports it.
type CatalogEntry struct {
	Namespace    string `json:"namespace"`
	Name         string `json:"name"`
	Description  string `json:"description"`
	IconURL      string `json:"icon_url"`
	Downloads    int64  `json:"download_count"`
	Ratings      int64  `json:"rating_count"`
	LastUpdated  string `json:"last_updated"`
	IsDeprecated bool   `json:"is_deprecated"`
	IsNSFW       bool   `json:"is_nsfw"`
	IsPinned     bool   `json:"is_pinned"`
}

// CatalogPage is one page of results.
type CatalogPage struct {
	Count   int            `json:"count"`
	Results []CatalogEntry `json:"results"`
}

// Orderings accepted by the listing endpoint, verified against the live API.
var Orderings = map[string]bool{
	"most-downloaded": true,
	"newest":          true,
	"top-rated":       true,
	"last-updated":    true,
}

// sectionCache remembers the per-community section ids.
type sectionCache struct {
	mu   sync.Mutex
	byID map[string][]Section
	at   map[string]time.Time
}

var sections = &sectionCache{byID: map[string][]Section{}, at: map[string]time.Time{}}

// sectionTTL is generous: sections change when a community is reconfigured,
// which is a once-a-year event.
const sectionTTL = 12 * time.Hour

// Sections returns a community's sections, cached.
func (c *Client) Sections(ctx context.Context, community string) ([]Section, error) {
	if !safeCommunity(community) {
		return nil, fmt.Errorf("mods: unusable community slug %q", community)
	}
	sections.mu.Lock()
	if list, ok := sections.byID[community]; ok && time.Since(sections.at[community]) < sectionTTL {
		sections.mu.Unlock()
		return list, nil
	}
	sections.mu.Unlock()

	var doc filtersDoc
	u := fmt.Sprintf("%s/api/cyberstorm/community/%s/filters/", c.apiBase, community)
	if err := c.getJSON(ctx, u, &doc); err != nil {
		return nil, err
	}

	sections.mu.Lock()
	sections.byID[community] = doc.Sections
	sections.at[community] = time.Now()
	sections.mu.Unlock()
	return doc.Sections, nil
}

// ModpacksSectionUUID returns the id of a community's "Modpacks" section.
func (c *Client) ModpacksSectionUUID(ctx context.Context, community string) (string, error) {
	list, err := c.Sections(ctx, community)
	if err != nil {
		return "", err
	}
	for _, s := range list {
		if strings.EqualFold(s.Slug, "modpacks") {
			return s.UUID, nil
		}
	}
	return "", fmt.Errorf("mods: у сообщества %q нет раздела модпаков", community)
}

// BrowseURL is the human-facing catalogue page, filtered and sorted the same
// way the panel shows it. The section is addressed by UUID because the site
// ignores the slug form.
func (c *Client) BrowseURL(ctx context.Context, community string) string {
	base := fmt.Sprintf("https://thunderstore.io/c/%s/?ordering=most-downloaded", community)
	uuid, err := c.ModpacksSectionUUID(ctx, community)
	if err != nil {
		return base
	}
	return base + "&section=" + uuid
}

// Catalog lists a community's modpacks.
//
// query and ordering are optional; page is 1-based. Only the four orderings the
// API actually accepts are passed through, because an unknown value is a 400
// and the panel would show an empty catalogue with no explanation.
func (c *Client) Catalog(ctx context.Context, community, sectionUUID, query, ordering string, page int) (*CatalogPage, error) {
	if !safeCommunity(community) {
		return nil, fmt.Errorf("mods: unusable community slug %q", community)
	}
	if page < 1 {
		page = 1
	}
	if !Orderings[ordering] {
		ordering = "most-downloaded"
	}

	v := url.Values{}
	v.Set("ordering", ordering)
	v.Set("page", strconv.Itoa(page))
	if sectionUUID != "" {
		v.Set("section", sectionUUID)
	}
	if q := strings.TrimSpace(query); q != "" {
		v.Set("q", q)
	}

	var out CatalogPage
	u := fmt.Sprintf("%s/api/cyberstorm/listing/%s/?%s", c.apiBase, community, v.Encode())
	if err := c.getJSON(ctx, u, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// packageURLRe matches a Thunderstore package page.
//
// Both the modern /c/{community}/p/{ns}/{name}/ form and the legacy
// /package/{ns}/{name}/ one are accepted: operators paste whichever the search
// engine gave them, and refusing the old shape reads as "the link is wrong".
var packageURLRe = regexp.MustCompile(
	`^https?://[^/]*thunderstore\.io/(?:c/([a-z0-9\-]+)/p|package)/([A-Za-z0-9_]+)/([A-Za-z0-9_]+)/?`)

// ParsePackageURL extracts the community (may be empty for the legacy form),
// namespace and name from a Thunderstore package link.
func ParsePackageURL(raw string) (community, namespace, name string, ok bool) {
	m := packageURLRe.FindStringSubmatch(strings.TrimSpace(raw))
	if m == nil {
		return "", "", "", false
	}
	return m[1], m[2], m[3], true
}

// safeCommunity guards a slug before it becomes part of a URL path.
func safeCommunity(s string) bool {
	if s == "" || len(s) > 100 {
		return false
	}
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9', r == '-':
		default:
			return false
		}
	}
	return true
}
