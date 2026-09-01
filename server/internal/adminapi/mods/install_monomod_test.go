package mods

import "testing"

// Маршрут, названный архивом явно, сильнее расширения .mm.dll — включая
// маршрут по умолчанию. Проверка «выбранный маршрут совпал с дефолтным» этого
// не различала, и сборочная зависимость плагина уезжала из его папки в
// BepInEx/monomod: у игрока плагин не находил свою сборку.
func TestMonoModDoesNotOverrideAnExplicitRoute(t *testing.T) {
	l, err := NewLayout(bepinexRules())
	if err != nil {
		t.Fatalf("NewLayout: %v", err)
	}
	mod := ResolvedPackage{Namespace: "Author", Name: "CoolMod"}
	subdir := mod.Namespace + "-" + mod.Name

	cases := []struct {
		what string
		rel  string
		want string
	}{
		{"полный путь до маршрута по умолчанию", "BepInEx/plugins/CoolMod/Something.mm.dll", "BepInEx/plugins/Author-CoolMod/CoolMod/Something.mm.dll"},
		{"маршрут по умолчанию одним листом", "plugins/Something.mm.dll", "BepInEx/plugins/Author-CoolMod/Something.mm.dll"},
		{"не дефолтный маршрут защищён и сейчас", "core/Something.mm.dll", "BepInEx/core/Author-CoolMod/Something.mm.dll"},
		{"россыпь без маршрута — это и есть MonoMod", "Something.mm.dll", "BepInEx/monomod/Author-CoolMod/Something.mm.dll"},
	}
	for _, c := range cases {
		t.Run(c.what, func(t *testing.T) {
			got, _, keep := l.destination(mod, c.rel, subdir)
			if !keep || got != c.want {
				t.Errorf("destination(%q) = (%q,%v), want (%q,true)", c.rel, got, keep, c.want)
			}
		})
	}
}
