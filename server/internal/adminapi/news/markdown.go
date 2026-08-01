package news

import "strings"

// ExtractMeta pulls the title (first H1), the summary (first paragraph) and the
// cover (first inline image) out of an article body.
func ExtractMeta(md string) (string, string, string) {
	lines := strings.Split(md, "\n")
	title := ""
	cover := ""
	var paras []string
	cur := ""
	for _, ln := range lines {
		s := strings.TrimRight(ln, "\r")
		// first image ![alt](url) if cover not set yet
		if cover == "" {
			ts2 := strings.TrimSpace(s)
			if i := strings.Index(ts2, "!["); i >= 0 {
				j := strings.Index(ts2[i:], "](")
				if j >= 0 {
					j = i + j
					k := strings.Index(ts2[j+2:], ")")
					if k >= 0 {
						k = j + 2 + k
						url := normalize(strings.TrimSpace(ts2[j+2 : k]))
						if url != "" {
							cover = url
						}
					}
				}
			}
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
	summary := ""
	if len(paras) > 0 {
		summary = paras[0]
	}
	return title, summary, cover
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
	if strings.HasPrefix(u, "assets/") {
		return "/assets/" + strings.TrimPrefix(u, "assets/")
	}
	// default: treat as /assets/<u>
	return "/assets/" + u
}

// mdToHTML is a very small markdown to HTML converter for the editor preview
// (H1/H2, paragraphs, code blocks, links, bold/italic).
func mdToHTML(md string) string {
	esc := func(s string) string {
		s = strings.ReplaceAll(s, "&", "&amp;")
		s = strings.ReplaceAll(s, "<", "&lt;")
		s = strings.ReplaceAll(s, ">", "&gt;")
		return s
	}
	// code blocks ```
	out := ""
	lines := strings.Split(md, "\n")
	inCode := false
	para := ""
	flushPara := func() {
		if strings.TrimSpace(para) != "" {
			out += "<p>" + inlineMD(esc(para)) + "</p>\n"
		}
		para = ""
	}
	for _, ln := range lines {
		if strings.HasPrefix(strings.TrimSpace(ln), "```") {
			if inCode {
				out += "</pre>\n"
				inCode = false
			} else {
				flushPara()
				out += "<pre>"
				inCode = true
			}
			continue
		}
		if inCode {
			out += esc(ln) + "\n"
			continue
		}
		s := strings.TrimRight(ln, "\r")
		if strings.HasPrefix(s, "# ") {
			flushPara()
			out += "<h1>" + inlineMD(esc(strings.TrimSpace(strings.TrimPrefix(s, "# ")))) + "</h1>\n"
			continue
		}
		if strings.HasPrefix(s, "## ") {
			flushPara()
			out += "<h2>" + inlineMD(esc(strings.TrimSpace(strings.TrimPrefix(s, "## ")))) + "</h2>\n"
			continue
		}
		if strings.TrimSpace(s) == "" {
			flushPara()
			continue
		}
		if para != "" {
			para += "\n"
		}
		para += s
	}
	if strings.TrimSpace(para) != "" {
		out += "<p>" + inlineMD(esc(para)) + "</p>\n"
	}
	return out
}

// inlineMD handles a very small subset (**bold**, *italic*, [text](url)).
func inlineMD(s string) string {
	// images ![alt](url)
	for {
		i := strings.Index(s, "![")
		if i < 0 {
			break
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			break
		}
		j = i + j
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			break
		}
		k = j + 2 + k
		alt := s[i+2 : j]
		url := normalize(s[j+2 : k])
		rep := "<img src=\"" + url + "\" alt=\"" + alt + "\" style=\"max-width:100%\">"
		s = s[:i] + rep + s[k+1:]
	}
	// bold **text**
	s = strings.ReplaceAll(s, "**", "\x00")
	parts := strings.Split(s, "\x00")
	for i := 1; i < len(parts); i += 2 {
		parts[i] = "<strong>" + parts[i] + "</strong>"
	}
	s = strings.Join(parts, "")
	// italic *text*
	s = strings.ReplaceAll(s, "*", "\x01")
	parts = strings.Split(s, "\x01")
	for i := 1; i < len(parts); i += 2 {
		parts[i] = "<em>" + parts[i] + "</em>"
	}
	s = strings.Join(parts, "")
	// links [text](url) (very naive)
	for {
		i := strings.Index(s, "[")
		if i < 0 {
			break
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			break
		}
		j = i + j
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			break
		}
		k = j + 2 + k
		text := s[i+1 : j]
		url := normalize(s[j+2 : k])
		rep := "<a href=\"" + url + "\" target=\"_blank\">" + text + "</a>"
		s = s[:i] + rep + s[k+1:]
	}
	return s
}
