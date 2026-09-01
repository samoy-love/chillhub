package news

import (
	"strings"
	"testing"
)

// Предпросмотр смотрят ровно затем, чтобы увидеть статью до публикации. У
// игрока markdown разбирает Markdig с расширениями, а здесь таблицы, списки и
// цитаты не разбирались вовсе и выходили абзацем с палками и дефисами — то
// есть проверить предпросмотром как раз то, что легко испортить, было нельзя.
func TestMdToHTMLRendersBlocksTheClientRenders(t *testing.T) {
	cases := []struct {
		what string
		md   string
		want []string
	}{
		{
			"таблица",
			"| Игра | Версия |\n| --- | ---: |\n| Chill | 1.0 |\n",
			[]string{"<table>", "<th>Игра</th>", "<th>Версия</th>", "<td>Chill</td>", "<td>1.0</td>", "</table>"},
		},
		{
			"маркированный список",
			"- первый\n- второй\n",
			[]string{"<ul>", "<li>первый</li>", "<li>второй</li>", "</ul>"},
		},
		{
			"нумерованный список",
			"1. первый\n2. второй\n",
			[]string{"<ol>", "<li>первый</li>", "<li>второй</li>", "</ol>"},
		},
		{
			"цитата",
			"> так сказал автор\n",
			[]string{"<blockquote>", "так сказал автор", "</blockquote>"},
		},
	}
	for _, c := range cases {
		t.Run(c.what, func(t *testing.T) {
			got := mdToHTML(c.md)
			for _, w := range c.want {
				if !strings.Contains(got, w) {
					t.Errorf("нет %q в:\n%s", w, got)
				}
			}
		})
	}
}

// Разметка внутри новых блоков проходит тот же экранировщик, что и абзацы:
// иначе таблица становится дырой ровно там, где её закрыли для текста.
func TestMdToHTMLEscapesInsideBlocks(t *testing.T) {
	for _, md := range []string{
		"| <b>жирно</b> | x |\n| --- | --- |\n| <script>alert(1)</script> | y |\n",
		"- <script>alert(1)</script>\n",
		"> <img src=\"x\" onerror=\"alert(1)\">\n",
	} {
		got := mdToHTML(md)
		if strings.Contains(got, "<script") || strings.Contains(got, "<b>") || strings.Contains(got, `onerror="`) {
			t.Errorf("сырой html пережил разбор %q:\n%s", md, got)
		}
	}
}

// Строка с палками — ещё не таблица: без разделителя под шапкой это обычный
// абзац, и Markdig у игрока считает так же.
func TestMdToHTMLKeepsPipesWithoutADelimiterRow(t *testing.T) {
	got := mdToHTML("сегодня 10 | 20 попыток\n")
	if strings.Contains(got, "<table") {
		t.Errorf("абзац превратился в таблицу:\n%s", got)
	}
	if !strings.Contains(got, "<p>") {
		t.Errorf("абзац потерялся:\n%s", got)
	}
}

// Незакрытая ограда — обычное состояние текста, который сейчас печатают:
// предпросмотр не должен уезжать в <pre> до конца статьи.
func TestMdToHTMLClosesAnUnterminatedCodeFence(t *testing.T) {
	got := mdToHTML("```\nкод\n")
	if strings.Count(got, "<pre>") != strings.Count(got, "</pre>") {
		t.Errorf("теги <pre> не сошлись:\n%s", got)
	}
}
