package news

import (
	"strings"
	"testing"
)

// The rendered HTML is what the admin panel shows in the preview, so a body
// that can break out of an attribute is a stored XSS in the panel.
func TestMdToHTMLEscapesAttributeBreakouts(t *testing.T) {
	cases := []string{
		`![a](/x" onerror="alert(1))`,
		`![a" onerror="alert(1)](/x)`,
		`[t](/x" onmouseover="alert(1))`,
		`[t" onmouseover="alert(1)](/x)`,
		`![a](/x' onerror='alert(1))`,
		`![a](/x" ><script src="https://evil.example/x.js">)`,
	}
	for _, md := range cases {
		got := mdToHTML(md)
		// The payload text may survive inside the attribute value; what must
		// not survive is a live handler, i.e. a quote that closed the value.
		for _, bad := range []string{`onerror="`, `onerror='`, `onmouseover="`, `onmouseover='`} {
			if strings.Contains(got, bad) {
				t.Errorf("event handler injected for %q:\n%s", md, got)
			}
		}
		if strings.Contains(got, "<script") {
			t.Errorf("script tag injected for %q:\n%s", md, got)
		}
		// Attributes must stay balanced: only the quotes we emit ourselves.
		if strings.Count(got, `"`)%2 != 0 {
			t.Errorf("unbalanced quotes for %q:\n%s", md, got)
		}
	}
}

func TestMdToHTMLEscapesTextAndKeepsMarkup(t *testing.T) {
	got := mdToHTML("# <b>hi</b> \"quoted\"\n\ntext & more\n")
	if strings.Contains(got, "<b>") {
		t.Errorf("raw html survived: %s", got)
	}
	if !strings.Contains(got, "&amp;") {
		t.Errorf("ampersand not escaped: %s", got)
	}
	if !strings.Contains(got, "<h1>") || !strings.Contains(got, "<p>") {
		t.Errorf("markdown structure lost: %s", got)
	}
}

func TestMdToHTMLRendersOrdinaryImageAndLink(t *testing.T) {
	got := mdToHTML("![cover](pic.png)\n\n[site](https://example.com)\n")
	if !strings.Contains(got, `<img src="/assets/pic.png" alt="cover"`) {
		t.Errorf("image not rendered: %s", got)
	}
	if !strings.Contains(got, `<a href="https://example.com"`) {
		t.Errorf("link not rendered: %s", got)
	}
}
