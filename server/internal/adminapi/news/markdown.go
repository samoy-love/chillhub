package news

import (
	"strings"
	"unicode"
	"unicode/utf8"
)

// summaryMaxRunes bounds the summary that goes into index.json. The launcher
// downloads that file on every start and draws two lines of the summary on a
// card, so an article whose opening paragraph is hundreds of kilobytes long
// must not be shipped in full to every client.
//
// The budget is counted in runes, not bytes: this project's articles are
// written in Russian, and a byte-sized cut lands in the middle of a two-byte
// character and puts a replacement glyph at the end of every card.
const summaryMaxRunes = 280

// ExtractMeta pulls the title (first H1), the summary (first paragraph that
// carries readable text, shortened to card size) and the cover (first inline
// image) out of an article body.
// firstImageURL returns the normalised target of the first ![alt](url) on a
// line, or "" when the line carries no complete image.
func firstImageURL(line string) string {
	s := strings.TrimSpace(line)
	i := strings.Index(s, "![")
	if i < 0 {
		return ""
	}
	j := strings.Index(s[i:], "](")
	if j < 0 {
		return ""
	}
	j += i
	k := strings.Index(s[j+2:], ")")
	if k < 0 {
		return ""
	}
	return normalize(strings.TrimSpace(s[j+2 : j+2+k]))
}

func ExtractMeta(md string) (string, string, string) {
	title := ""
	cover := ""
	var paras []string
	cur := ""
	for ln := range strings.SplitSeq(md, "\n") {
		s := strings.TrimRight(ln, "\r")
		// first image ![alt](url) if cover not set yet
		if cover == "" {
			cover = firstImageURL(s)
		}
		if strings.HasPrefix(s, "# ") && title == "" {
			title = strings.TrimSpace(strings.TrimPrefix(s, "# "))
			continue
		}
		if strings.TrimSpace(s) == "" {
			if strings.TrimSpace(cur) != "" {
				paras = append(paras, strings.TrimSpace(cur))
				cur = ""
			}
		} else {
			if cur != "" {
				cur += "\n"
			}
			cur += s
		}
	}
	if strings.TrimSpace(cur) != "" {
		paras = append(paras, strings.TrimSpace(cur))
	}
	return title, summarize(paras), cover
}

// summarize picks the first block that carries readable text and shortens it.
//
// Articles here almost always open with the cover image on the first line,
// often followed by a subheading. Taking the first block verbatim therefore put
// strings like "![c](pic.png)" into index.json, and both the launcher and the
// landing page render the summary as plain text — the raw markdown showed up on
// the card exactly as typed.
func summarize(paras []string) string {
	inCode := false
	for _, p := range paras {
		var s string
		s, inCode = summaryText(p, inCode)
		if s != "" {
			return truncateSummary(s)
		}
	}
	return ""
}

// summaryText reduces one block to the text a card would show, or to "" when
// the block is pure markup: an image, a heading, a horizontal rule, a fence.
//
// The fence state is threaded through the blocks because a code listing with a
// blank line in it is several blocks, and a build log is not a summary.
func summaryText(block string, inCode bool) (string, bool) {
	var out []string
	for ln := range strings.SplitSeq(block, "\n") {
		s := strings.TrimSpace(ln)
		if isFenceLine(s) {
			inCode = !inCode
			continue
		}
		if inCode || s == "" || isHeadingLine(s) || isRuleLine(s) {
			continue
		}
		s = strings.TrimSpace(stripInlineMarkup(stripBlockMarkers(s)))
		if s != "" {
			out = append(out, s)
		}
	}
	return strings.Join(out, "\n"), inCode
}

// truncateSummary shortens s to summaryMaxRunes, preferring a word boundary and
// marking the cut with an ellipsis so the card does not read as a sentence that
// simply stops.
func truncateSummary(s string) string {
	if utf8.RuneCountInString(s) <= summaryMaxRunes {
		return s
	}
	r := []rune(s)[:summaryMaxRunes]
	cut := len(r)
	for i := len(r) - 1; i > 0; i-- {
		if unicode.IsSpace(r[i]) {
			cut = i
			break
		}
	}
	// A "word" that eats more than half the budget is not a word but a URL or a
	// hash; backing off to its start would leave an almost empty summary, so cut
	// through it instead.
	if cut < summaryMaxRunes/2 {
		cut = len(r)
	}
	return strings.TrimRightFunc(string(r[:cut]), unicode.IsSpace) + "…"
}

// isHeadingLine reports whether s is an ATX heading (# … ######).
func isHeadingLine(s string) bool {
	i := 0
	for i < len(s) && s[i] == '#' {
		i++
	}
	return i > 0 && i <= 6 && (i == len(s) || s[i] == ' ' || s[i] == '\t')
}

// isRuleLine reports whether s is a horizontal rule (---, ***, ___).
func isRuleLine(s string) bool {
	t := strings.Map(func(r rune) rune {
		if r == ' ' || r == '\t' {
			return -1
		}
		return r
	}, s)
	if len(t) < 3 {
		return false
	}
	for _, c := range []string{"-", "*", "_"} {
		if strings.Trim(t, c) == "" {
			return true
		}
	}
	return false
}

// isFenceLine reports whether s opens or closes a code block.
func isFenceLine(s string) bool {
	return strings.HasPrefix(s, "```") || strings.HasPrefix(s, "~~~")
}

// stripBlockMarkers removes the quote and list prefixes of a line. A bullet
// list is readable text once the bullets are gone, so it makes a fine summary.
func stripBlockMarkers(s string) string {
	for {
		t := strings.TrimSpace(s)
		if t == ">" {
			return ""
		}
		if strings.HasPrefix(t, "> ") {
			s = t[2:]
			continue
		}
		if strings.HasPrefix(t, "- ") || strings.HasPrefix(t, "* ") || strings.HasPrefix(t, "+ ") {
			s = t[2:]
			continue
		}
		if n := listOrdinalLen(t); n > 0 {
			s = t[n:]
			continue
		}
		return t
	}
}

// listOrdinalLen returns the length of a leading "12. " or "12) " marker.
func listOrdinalLen(s string) int {
	i := 0
	for i < len(s) && s[i] >= '0' && s[i] <= '9' {
		i++
	}
	if i == 0 || i+1 >= len(s) {
		return 0
	}
	if (s[i] == '.' || s[i] == ')') && s[i+1] == ' ' {
		return i + 2
	}
	return 0
}

// stripInlineMarkup turns inline markdown into the text it renders as: images
// disappear, links keep their label, emphasis loses its punctuation. The single
// underscore is deliberately left alone — it is far more often part of a
// filename than an italic marker.
func stripInlineMarkup(s string) string {
	s = dropImages(s)
	s = unwrapLinks(s)
	for _, m := range []string{"**", "__", "~~", "`", "*"} {
		s = strings.ReplaceAll(s, m, "")
	}
	return s
}

// dropImages removes every ![alt](url) from s.
func dropImages(s string) string {
	for {
		i := strings.Index(s, "![")
		if i < 0 {
			return s
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			return s
		}
		j += i
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			return s
		}
		s = s[:i] + s[j+2+k+1:]
	}
}

// unwrapLinks replaces every [label](url) with its label.
func unwrapLinks(s string) string {
	var out strings.Builder
	for {
		i := strings.Index(s, "[")
		if i < 0 {
			break
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			break
		}
		j += i
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			break
		}
		out.WriteString(s[:i])
		out.WriteString(s[i+1 : j])
		s = s[j+2+k+1:]
	}
	return out.String() + s
}

// normalize turns a markdown image/link target into a web path, defaulting
// relative names into the /assets/ tree.
func normalize(u string) string {
	u = strings.TrimSpace(u)
	if u == "" {
		return u
	}
	if strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://") {
		return u
	}
	u = strings.TrimPrefix(u, "./")
	if strings.HasPrefix(u, "/") {
		return u
	}
	if rest, ok := strings.CutPrefix(u, "assets/"); ok {
		return "/assets/" + rest
	}
	// default: treat as /assets/<u>
	return "/assets/" + u
}

// mdToHTML converts an article body to the HTML of the editor preview.
//
// WHAT THIS IS AND IS NOT. The launcher does not receive this HTML: it fetches
// the raw .md and renders it itself (NewsPageRenderer, Markdig with the
// advanced extensions). So this converter is an approximation of the client
// renderer, and the point of the preview is that an editor can tell before
// publishing what the article will look like. Everything the launcher turns
// into a block — headings, paragraphs, code fences, lists, quotes, tables —
// has to become the same tag here, or the preview shows a wall of pipes and
// dashes for exactly the articles that needed checking.
//
// The one deliberate difference is raw HTML: it is escaped here (see
// escapeHTML) rather than passed through.
func mdToHTML(md string) string {
	var out strings.Builder
	var para, quote, list []string
	listTag := ""
	inCode := false

	flushPara := func() {
		if len(para) > 0 {
			out.WriteString("<p>" + inlineMD(escapeHTML(strings.Join(para, "\n"))) + "</p>\n")
			para = nil
		}
	}
	flushQuote := func() {
		if len(quote) > 0 {
			out.WriteString("<blockquote><p>" + inlineMD(escapeHTML(strings.Join(quote, "\n"))) + "</p></blockquote>\n")
			quote = nil
		}
	}
	flushList := func() {
		if listTag == "" {
			return
		}
		out.WriteString("<" + listTag + ">\n")
		for _, it := range list {
			out.WriteString("<li>" + inlineMD(escapeHTML(it)) + "</li>\n")
		}
		out.WriteString("</" + listTag + ">\n")
		list, listTag = nil, ""
	}
	flushBlocks := func() {
		flushPara()
		flushQuote()
		flushList()
	}

	lines := strings.Split(md, "\n")
	for i := 0; i < len(lines); i++ {
		s := strings.TrimRight(lines[i], "\r")
		// code blocks ```
		if strings.HasPrefix(strings.TrimSpace(s), "```") {
			if inCode {
				out.WriteString("</pre>\n")
			} else {
				flushBlocks()
				out.WriteString("<pre>")
			}
			inCode = !inCode
			continue
		}
		if inCode {
			out.WriteString(escapeHTML(s) + "\n")
			continue
		}
		if h := headingHTML(s); h != "" {
			flushBlocks()
			out.WriteString(h)
			continue
		}
		if strings.TrimSpace(s) == "" {
			flushBlocks()
			continue
		}
		if item, tag, ok := listItem(s); ok {
			flushPara()
			flushQuote()
			if listTag != "" && listTag != tag {
				flushList()
			}
			listTag = tag
			list = append(list, item)
			continue
		}
		if q, ok := quoteLine(s); ok {
			flushPara()
			flushList()
			quote = append(quote, q)
			continue
		}
		if rows, n := tableAt(lines, i); n > 0 {
			flushBlocks()
			out.WriteString(tableHTML(rows))
			i += n - 1
			continue
		}
		flushQuote()
		flushList()
		para = append(para, s)
	}
	// An unterminated fence is what the editor sees while typing one; closing
	// it keeps the preview from swallowing the rest of the article.
	if inCode {
		out.WriteString("</pre>\n")
	}
	flushBlocks()
	return out.String()
}

// listItem recognises one list line and returns its text and the tag the list
// needs. Indentation is ignored: nesting is a level of detail the preview does
// not promise, but a bulleted list drawn as a bulleted list is.
func listItem(s string) (item, tag string, ok bool) {
	t := strings.TrimLeft(s, " \t")
	for _, m := range []string{"- ", "* ", "+ "} {
		if rest, cut := strings.CutPrefix(t, m); cut {
			return strings.TrimSpace(rest), "ul", true
		}
	}
	// "12. текст" — the number itself is dropped: <ol> renumbers anyway.
	digits := 0
	for digits < len(t) && t[digits] >= '0' && t[digits] <= '9' {
		digits++
	}
	if digits > 0 && strings.HasPrefix(t[digits:], ". ") {
		return strings.TrimSpace(t[digits+2:]), "ol", true
	}
	return "", "", false
}

// quoteLine recognises one blockquote line and returns its text.
func quoteLine(s string) (string, bool) {
	t := strings.TrimLeft(s, " \t")
	if rest, ok := strings.CutPrefix(t, ">"); ok {
		return strings.TrimPrefix(rest, " "), true
	}
	return "", false
}

// tableAt reads a pipe table starting at lines[i] and returns its rows (the
// header first) and how many lines it consumed. n is 0 when there is no table:
// a header row alone is just a paragraph with pipes in it, so the delimiter row
// below it is what makes a table a table.
func tableAt(lines []string, i int) (rows [][]string, n int) {
	if i+1 >= len(lines) || !strings.Contains(lines[i], "|") || !isTableDelimiter(lines[i+1]) {
		return nil, 0
	}
	rows = append(rows, tableCells(lines[i]))
	n = 2
	for j := i + 2; j < len(lines); j++ {
		s := strings.TrimRight(lines[j], "\r")
		if !strings.Contains(s, "|") || strings.TrimSpace(s) == "" {
			break
		}
		rows = append(rows, tableCells(s))
		n++
	}
	return rows, n
}

// isTableDelimiter reports whether s is the |---|:--:| row under a table head.
func isTableDelimiter(s string) bool {
	cells := tableCells(s)
	if len(cells) == 0 {
		return false
	}
	for _, c := range cells {
		c = strings.TrimPrefix(strings.TrimSuffix(c, ":"), ":")
		if c == "" || strings.Trim(c, "-") != "" {
			return false
		}
	}
	return true
}

// tableCells splits one table row on the pipes, dropping the optional outer
// ones.
func tableCells(s string) []string {
	t := strings.TrimSpace(strings.TrimRight(s, "\r"))
	t = strings.TrimSuffix(strings.TrimPrefix(t, "|"), "|")
	if strings.TrimSpace(t) == "" {
		return nil
	}
	cells := strings.Split(t, "|")
	for i := range cells {
		cells[i] = strings.TrimSpace(cells[i])
	}
	return cells
}

// tableHTML renders the rows tableAt collected.
func tableHTML(rows [][]string) string {
	if len(rows) == 0 {
		return ""
	}
	var b strings.Builder
	b.WriteString("<table>\n<thead>\n")
	writeRow(&b, rows[0], "th")
	b.WriteString("</thead>\n<tbody>\n")
	for _, r := range rows[1:] {
		writeRow(&b, r, "td")
	}
	b.WriteString("</tbody>\n</table>\n")
	return b.String()
}

// writeRow writes one table row with the given cell tag.
func writeRow(b *strings.Builder, cells []string, cell string) {
	b.WriteString("<tr>")
	for _, c := range cells {
		b.WriteString("<" + cell + ">" + inlineMD(escapeHTML(c)) + "</" + cell + ">")
	}
	b.WriteString("</tr>\n")
}

// headingHTML renders an H1 or H2 line, or returns "" when s is neither.
func headingHTML(s string) string {
	switch {
	case strings.HasPrefix(s, "# "):
		return "<h1>" + inlineMD(escapeHTML(strings.TrimSpace(strings.TrimPrefix(s, "# ")))) + "</h1>\n"
	case strings.HasPrefix(s, "## "):
		return "<h2>" + inlineMD(escapeHTML(strings.TrimSpace(strings.TrimPrefix(s, "## ")))) + "</h2>\n"
	}
	return ""
}

// escapeHTML makes text safe both between tags and inside a double- or
// single-quoted attribute value.
//
// The quotes matter: everything mdToHTML produces ends up in src="…", href="…"
// or alt="…", and without escaping them a body such as
//
//	![a](/x" onerror="alert(1))
//
// closes the attribute early and injects an event handler that the admin
// preview then executes. The launcher renders the same body through its own
// converter, so this escaping speaks for the preview only — the client end is
// the launcher's to guard.
func escapeHTML(s string) string {
	s = strings.ReplaceAll(s, "&", "&amp;")
	s = strings.ReplaceAll(s, "<", "&lt;")
	s = strings.ReplaceAll(s, ">", "&gt;")
	s = strings.ReplaceAll(s, `"`, "&#34;")
	s = strings.ReplaceAll(s, "'", "&#39;")
	return s
}

// escapeQuotes escapes only the quote characters. inlineMD applies it to the
// substrings it cuts out of already-escaped text (URLs, alt texts, link
// labels): running the full escaper again would turn &amp; into &amp;amp;,
// while quotes cannot appear in escaped output at all, so this stays correct
// even if inlineMD is ever handed raw text.
func escapeQuotes(s string) string {
	s = strings.ReplaceAll(s, `"`, "&#34;")
	s = strings.ReplaceAll(s, "'", "&#39;")
	return s
}

// inlineMD handles a very small subset (**bold**, *italic*, [text](url)).
// It expects text that has already been through escapeHTML.
func inlineMD(s string) string {
	s = renderImages(s)
	// bold before italic: "**" has to be consumed before a lone "*" is.
	s = wrapDelimited(s, "**", "<strong>", "</strong>")
	s = wrapDelimited(s, "*", "<em>", "</em>")
	return renderLinks(s)
}

// wrapDelimited puts every stretch of s between a pair of delimiters between
// the given tags. An unpaired trailing delimiter is dropped, which is what the
// editor sees while a word is still being typed.
func wrapDelimited(s, delim, openTag, closeTag string) string {
	parts := strings.Split(s, delim)
	for i := 1; i < len(parts); i += 2 {
		parts[i] = openTag + parts[i] + closeTag
	}
	return strings.Join(parts, "")
}

// renderImages replaces every ![alt](url) with an <img>.
func renderImages(s string) string {
	for {
		i := strings.Index(s, "![")
		if i < 0 {
			return s
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			return s
		}
		j += i
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			return s
		}
		k = j + 2 + k
		alt := escapeQuotes(s[i+2 : j])
		url := escapeQuotes(normalize(s[j+2 : k]))
		rep := "<img src=\"" + url + "\" alt=\"" + alt + "\" style=\"max-width:100%\">"
		s = s[:i] + rep + s[k+1:]
	}
}

// renderLinks replaces every [text](url) with an <a> (very naive).
func renderLinks(s string) string {
	for {
		i := strings.Index(s, "[")
		if i < 0 {
			return s
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			return s
		}
		j += i
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			return s
		}
		k = j + 2 + k
		text := escapeQuotes(s[i+1 : j])
		url := escapeQuotes(normalize(s[j+2 : k]))
		rep := "<a href=\"" + url + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + text + "</a>"
		s = s[:i] + rep + s[k+1:]
	}
}
