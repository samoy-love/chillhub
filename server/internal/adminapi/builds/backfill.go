package builds

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"ChillHub/server/internal/adminutil"
)

// Дозаполнение хешей блоков в манифестах, опубликованных до их появления.
//
// Лаунчеру для блочного обновления нужны блоки только НОВОЙ версии: блоки
// своей старой копии он считает сам, с диска. Поэтому достаточно дописать
// хеши в манифесты версий, которые игроки получают сейчас или получат, — а
// заново публиковать ради этого многогигабайтные сборки незачем.

// BackfillResult — что сделано с одним манифестом.
type BackfillResult struct {
	// Files — скольким файлам дописаны хеши блоков.
	Files int
	// Bytes — сколько байт ради этого прочитано с диска.
	Bytes int64
	// Skipped — почему манифест не тронут; пусто, если он переписан.
	Skipped string
}

// ErrManifestChanged — манифест переписали, пока считались хеши.
var ErrManifestChanged = errors.New("manifest changed while block hashes were being computed")

// backfillHashed зовётся между подсчётом хешей и записью манифеста. Нужна
// тестам: иначе перепубликацию «посреди подсчёта» не воспроизвести.
var backfillHashed = func() {}

// BackfillBlocks дописывает хеши блоков в манифест версии.
//
// Хеши считаются по файлам, которые сейчас раздаются, и каждый файл заодно
// сверяется со своими SHA-256 и Blake3 из манифеста. Не сошёлся хоть один —
// манифест не трогается вовсе: блоки, посчитанные по другому содержимому,
// направили бы лаунчер собирать не тот файл, и спасла бы только итоговая
// сверка, ценой полной загрузки.
//
// Перед записью манифест перечитывается и сравнивается побайтно с тем, по
// которому считали. Команда запускается отдельным процессом, мимо блокировки
// публикации в админке, и без этой проверки перепубликация той же версии,
// пришедшаяся на время подсчёта, была бы затёрта старым списком файлов.
func (h *Handlers) BackfillBlocks(ns Namespace, gid, ver string) (BackfillResult, error) {
	// Имена приходят с диска и из командной строки, а превращаются в пути.
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		return BackfillResult{}, fmt.Errorf("unsafe gameId %q or version %q", gid, ver)
	}
	unlock := lockPublish(string(ns)+"/"+gid, ver)
	defer unlock()

	manDir := h.ManifestsDirFor(ns, gid)
	manPath := filepath.Join(manDir, ver+".json")
	before, err := os.ReadFile(manPath)
	if err != nil {
		return BackfillResult{}, err
	}
	var m manifest
	if err := json.Unmarshal(before, &m); err != nil {
		return BackfillResult{}, fmt.Errorf("parse %s: %w", manPath, err)
	}

	todo, skipped := backfillTodo(m)
	if skipped != "" {
		return BackfillResult{Skipped: skipped}, nil
	}

	filesRoot := filepath.Join(h.ContentDirFor(ns, gid), ver, "files")
	sums := make([]fileSums, len(todo))
	if err := hashAll(len(todo), func(k int) error {
		s, herr := hashServedFile(filesRoot, m.Files[todo[k]])
		sums[k] = s
		return herr
	}, func(int) {}); err != nil {
		return BackfillResult{}, err
	}

	res := BackfillResult{}
	for k, i := range todo {
		m.Files[i].Blocks = encodeBlocks(sums[k].blocks, m.Files[i].Size)
		res.Files++
		res.Bytes += m.Files[i].Size
	}
	m.BlockSize = BlockSize

	backfillHashed()
	if err := h.writeIfUnchanged(manDir, manPath, before, m); err != nil {
		return BackfillResult{}, err
	}
	return res, nil
}

// backfillTodo выбирает файлы манифеста, которым нужны хеши блоков, или
// объясняет, почему манифест трогать не нужно.
func backfillTodo(m manifest) ([]int, string) {
	if m.BlockSize != 0 && m.BlockSize != BlockSize {
		// Хеши блоков другого размера здесь посчитать нечем: сервер режет
		// только по BlockSize. Такой манифест мог написать лишь будущий сервер.
		return nil, fmt.Sprintf("blockSize %d differs from %d", m.BlockSize, BlockSize)
	}
	var todo []int
	for i, f := range m.Files {
		if f.Blocks == "" && blockCount(f.Size, BlockSize) >= 2 {
			todo = append(todo, i)
		}
	}
	if len(todo) == 0 {
		return nil, "nothing to add"
	}
	return todo, ""
}

// hashServedFile считает хеши раздаваемого файла и сверяет его с записью
// манифеста: размер, SHA-256, Blake3.
func hashServedFile(filesRoot string, f manifestFile) (fileSums, error) {
	// Путь уже прошёл проверку при публикации, но читается он с диска по
	// строке из файла, который мог поправить кто угодно с доступом к нему.
	if why := pathProblem(f.Path); why != "" {
		return fileSums{}, fmt.Errorf("%s: %s", f.Path, why)
	}
	s, err := hashFile(filepath.Join(filesRoot, filepath.FromSlash(f.Path)))
	if err != nil {
		return fileSums{}, err
	}
	shaOK := f.Sha256 == "" || strings.EqualFold(s.sha256, f.Sha256)
	b3OK := f.Blake3 == "" || strings.EqualFold(s.blake3, f.Blake3)
	if s.size != f.Size || !shaOK || !b3OK {
		return fileSums{}, fmt.Errorf("%s: file on disk does not match the manifest", f.Path)
	}
	return s, nil
}

// writeIfUnchanged пишет манифест, только если на диске лежит ровно тот,
// по которому считали (before).
func (h *Handlers) writeIfUnchanged(manDir, manPath string, before []byte, m manifest) error {
	now, err := os.ReadFile(manPath)
	if err != nil {
		return err
	}
	if !bytes.Equal(before, now) {
		return ErrManifestChanged
	}
	_, _, err = h.writeManifestTo(manDir, m, false)
	return err
}

// BackfillTarget — одна версия, которой можно дописать хеши блоков.
type BackfillTarget struct {
	NS      Namespace
	GameID  string
	Version string
}

// BackfillTargets перечисляет опубликованные версии игр и модпаков.
//
// Лаунчер пропускается: себя он обновляет своим путём, без блоков, а его
// манифест сверяет ещё и апдейтер — трогать его ради ненужного поля незачем.
// Пустые gid и ver — «все»; заданные сужают выбор.
func (h *Handlers) BackfillTargets(gid, ver string) ([]BackfillTarget, error) {
	var out []BackfillTarget
	for _, ns := range []Namespace{NamespaceGame, NamespaceMods} {
		root := filepath.Join(h.root, "manifests")
		if ns != NamespaceGame {
			root = filepath.Join(root, string(ns))
		}
		games, err := os.ReadDir(root)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			return nil, err
		}
		for _, g := range games {
			name := g.Name()
			if !g.IsDir() || strings.HasPrefix(name, "_") || name == "launcher" {
				continue
			}
			if gid != "" && name != gid {
				continue
			}
			entries, err := os.ReadDir(filepath.Join(root, name))
			if err != nil {
				return nil, err
			}
			vers := manifestVersions(entries)
			sort.Strings(vers)
			for _, v := range vers {
				if ver != "" && v != ver {
					continue
				}
				out = append(out, BackfillTarget{NS: ns, GameID: name, Version: v})
			}
		}
	}
	return out, nil
}
