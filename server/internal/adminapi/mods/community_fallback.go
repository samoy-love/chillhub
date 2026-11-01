package mods

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"strings"
)

// ResolveListInCommunity keeps the game's boundary even when the index could
// not be fetched. Roots remain explicit operator choices; their dependencies
// must be listed in this community before their pinned versions can be used.
func (c *Client) ResolveListInCommunity(
	ctx context.Context, eco *Ecosystem, roots []string, prog ResolveProgress, community string, idx *CommunityIndex,
) (*Resolution, error) {
	if !safeCommunity(community) {
		return nil, fmt.Errorf("mods: unusable community slug %q", community)
	}
	if idx != nil && idx.Community() != community {
		return nil, fmt.Errorf("mods: index belongs to %q, not %q", idx.Community(), community)
	}
	return c.resolveFrom(ctx, eco, roots, prog, idx, community)
}

// communityListingIdentity is deliberately separate from version metadata:
// membership can change, so the immutable version cache cannot prove it.
type communityListingIdentity struct {
	Community string `json:"community_identifier"` //nolint:tagliatelle // Thunderstore's external field name.
	Namespace string `json:"namespace"`
	Name      string `json:"name"`
}

// getCommunityDependency returns foreign=true only after every possible
// namespace/name split has a confirmed 404 in the community. Network failures,
// invalid JSON and unexpected identities abort the walk, never bypass the gate.
// Querying the package without a version preserves the distinction between a
// foreign package and a missing pinned version of a package that belongs here.
func (c *Client) getCommunityDependency(ctx context.Context, community, dep string) (*PackageVersion, bool, error) {
	candidates := SplitCandidates(dep)
	if len(candidates) == 0 {
		return nil, false, fmt.Errorf("mods: malformed dependency %q", dep)
	}
	for _, part := range candidates {
		u := fmt.Sprintf("%s/api/cyberstorm/listing/%s/%s/%s/", c.apiBase,
			url.PathEscape(community), url.PathEscape(part.Namespace), url.PathEscape(part.Name))
		var listing communityListingIdentity
		err := c.getJSON(ctx, u, &listing)
		if errors.Is(err, ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, false, err
		}
		if listing.Community != community || !strings.EqualFold(listing.Namespace, part.Namespace) ||
			!strings.EqualFold(listing.Name, part.Name) {
			return nil, false, fmt.Errorf("mods: inconsistent community listing for %s in %s", dep, community)
		}
		// Keep the split that passed the community check: a different split
		// may also exist globally, but belong to another game.
		v, err := c.GetVersion(ctx, listing.Namespace, listing.Name, part.Version)
		return v, false, err
	}
	return nil, true, nil
}
