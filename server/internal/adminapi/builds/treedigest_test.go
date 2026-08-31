package builds

import "testing"

// ОТПЕЧАТОК ДЕРЕВА — ДОГОВОР МЕЖДУ ДВУМЯ ЯЗЫКАМИ.
//
// Сервер считает его здесь и кладёт в /api/games; лаунчер считает свой по
// установленному манифесту (ChillHub.Core.Mods.ModPackDigest) и сравнивает
// напрямую. Разъедутся реализации — лаунчер либо перестанет замечать
// пересборки, либо начнёт звать обновляться на каждой проверке, и обе поломки
// молчаливые.
//
// Поэтому одно и то же ожидаемое значение прибито с обеих сторон: здесь и в
// ModPackDigestTests. Меняете алгоритм — меняете оба, иначе тест по ту сторону
// покраснеет и объяснит, почему.
const digestFixture = "fffdf2012157dea2d60a57ffb6797bb8"

func digestFixtureFiles() []manifestFile {
	// Порядок нарочно не отсортированный, и один путь не-ASCII: сортировка
	// обязана идти по БАЙТАМ UTF-8, иначе Go и C# разойдутся на кириллице.
	return []manifestFile{
		{Path: "b.txt", Blake3: "bbb"},
		{Path: "BepInEx/plugins/Автор-Мод/мод.dll", Blake3: "aaa"},
		{Path: "a.txt", Blake3: "ccc"},
	}
}

func TestTreeDigestIsStableAndOrderIndependent(t *testing.T) {
	files := digestFixtureFiles()
	got := treeDigest(files)
	if got != digestFixture {
		t.Errorf("treeDigest = %q, ожидалось %q — договор с лаунчером нарушен", got, digestFixture)
	}

	shuffled := []manifestFile{files[2], files[0], files[1]}
	if again := treeDigest(shuffled); again != got {
		t.Errorf("порядок файлов изменил отпечаток: %q vs %q", again, got)
	}

	// Входной срез не должен меняться под вызывающим: манифест после этого
	// пишется на диск, и переставленный список файлов в нём — не то, что кто-то
	// заказывал.
	if files[0].Path != "b.txt" {
		t.Error("treeDigest переставил файлы во входном срезе")
	}
}

func TestTreeDigestFollowsContentNotNames(t *testing.T) {
	files := digestFixtureFiles()
	base := treeDigest(files)

	changed := digestFixtureFiles()
	changed[0].Blake3 = "другой хеш"
	if treeDigest(changed) == base {
		t.Error("отпечаток не заметил изменившийся файл — пересборка не доедет до игроков")
	}

	// Размер и флаг исполняемости в отпечаток не входят: содержимое описывает
	// хеш, а два файла с одним хешем и разным размером — это испорченный
	// манифест, а не повод звать всех обновляться.
	renamed := digestFixtureFiles()
	renamed[1].Size = 999
	if treeDigest(renamed) != base {
		t.Error("отпечаток изменился от поля, которое содержимого не описывает")
	}

	added := append(digestFixtureFiles(), manifestFile{Path: "c.txt", Blake3: "ddd"})
	if treeDigest(added) == base {
		t.Error("отпечаток не заметил новый файл")
	}
}
