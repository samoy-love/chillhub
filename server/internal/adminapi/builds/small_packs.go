package builds

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"ChillHub/server/internal/adminutil"
	"github.com/zeebo/blake3"
)

// Each index refers to the published files array. A pack contains just their
// bytes in that order. The individual files are always published alongside it.
type smallFilePack struct {
	SHA256 string `json:"sha256"`
	Files  []int  `json:"files"`
}

const (
	maxSmallFileBytes = 64 << 10
	maxPackBytes      = 512 << 10
	maxPackFiles      = 128
	minPackFiles      = 8
	targetPackCount   = 8
)

func smallFileGroups(files []manifestFile) [][]int {
	var eligible []int
	var total int64
	for i, file := range files {
		if file.Size <= 0 || file.Size > maxSmallFileBytes {
			continue
		}
		eligible = append(eligible, i)
		total += file.Size
	}
	if len(eligible) < minPackFiles {
		return nil
	}

	// Aim to keep the usual eight download workers busy. Further groups may
	// be needed to stay within the byte or file limit. Small catalogs use as
	// many groups as they can fill without creating tiny requests.
	fileGroups := (len(eligible) + maxPackFiles - 1) / maxPackFiles
	byteGroups := int((total + maxPackBytes - 1) / maxPackBytes)
	remainingGroups := min(len(eligible)/minPackFiles, max(targetPackCount, fileGroups, byteGroups))
	groups := make([][]int, 0, remainingGroups)
	sizes := make([]int64, 0, remainingGroups)
	for len(eligible) >= minPackFiles {
		count, size := nextSmallFileGroup(files, eligible, remainingGroups)
		groups = append(groups, eligible[:count:count])
		sizes = append(sizes, size)
		eligible = eligible[count:]
		remainingGroups = max(1, remainingGroups-1)
	}

	// A few large members can force a short tail despite the balanced
	// counts. Reuse any space in earlier packs before leaving files separate.
	for _, index := range eligible {
		for group := len(groups) - 1; group >= 0; group-- {
			if len(groups[group]) < maxPackFiles && sizes[group]+files[index].Size <= maxPackBytes {
				groups[group] = append(groups[group], index)
				sizes[group] += files[index].Size
				break
			}
		}
	}
	return groups
}

func nextSmallFileGroup(files []manifestFile, eligible []int, remainingGroups int) (int, int64) {
	target := min(maxPackFiles, max(minPackFiles, (len(eligible)+remainingGroups-1)/remainingGroups))
	var count int
	var size int64
	for count < target {
		next := files[eligible[count]].Size
		if size+next > maxPackBytes {
			break
		}
		size += next
		count++
	}
	if left := len(eligible) - count; left > 0 && left < minPackFiles && len(eligible) >= 2*minPackFiles {
		// Both groups can contain eight files, which always fit individually.
		for count > len(eligible)-minPackFiles {
			count--
			size -= files[eligible[count]].Size
		}
	}
	return count, size
}

// Call before promoting the version: a published manifest must never refer to
// a half-written pack. Rebuilding the same version produces the same addresses.
func writeSmallPacks(versionDir string, m manifest) ([]smallFilePack, error) {
	if strings.EqualFold(strings.TrimSpace(m.GameID), LauncherGameID) {
		return nil, nil
	}
	groups := smallFileGroups(m.Files)
	if len(groups) == 0 {
		return nil, nil
	}
	version, err := os.OpenRoot(versionDir)
	if err != nil {
		return nil, err
	}
	defer func() { _ = version.Close() }()
	filesRoot, err := version.OpenRoot("files")
	if err != nil {
		return nil, err
	}
	defer func() { _ = filesRoot.Close() }()
	if err := version.Mkdir("packs", contentDirPerm); err != nil && !errors.Is(err, os.ErrExist) {
		return nil, err
	}
	entry, err := version.Lstat("packs")
	if err != nil {
		return nil, err
	}
	if !entry.IsDir() {
		return nil, fmt.Errorf("pack destination in %q is not a directory", versionDir)
	}

	packs := make([]smallFilePack, 0, len(groups))
	var payload []byte
	for _, group := range groups {
		payload = payload[:0]
		for _, index := range group {
			data, err := readPackMember(filesRoot, m.Files[index])
			if err != nil {
				return nil, err
			}
			payload = append(payload, data...)
		}
		digest := sha256.Sum256(payload)
		name := hex.EncodeToString(digest[:])
		path := filepath.Join(versionDir, "packs", name+".bin")
		if err := storeSmallPack(path, payload); err != nil {
			return nil, err
		}
		packs = append(packs, smallFilePack{SHA256: name, Files: group})
	}
	return packs, nil
}

// A backfill also repairs missing or corrupt objects. Avoid replacing a
// verified file while another client is downloading it.
func storeSmallPack(path string, payload []byte) error {
	info, err := os.Lstat(path)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if err == nil && info.Mode().IsRegular() && info.Size() == int64(len(payload)) {
		file, err := os.Open(path)
		if err != nil {
			return err
		}
		existing, readErr := io.ReadAll(io.LimitReader(file, int64(len(payload))+1))
		closeErr := file.Close()
		if err := errors.Join(readErr, closeErr); err != nil {
			return err
		}
		if bytes.Equal(existing, payload) {
			return nil
		}
	}
	return adminutil.WriteFileAtomic(path, payload, contentFilePerm)
}

func readPackMember(root *os.Root, file manifestFile) ([]byte, error) {
	if why := pathProblem(file.Path); why != "" {
		return nil, fmt.Errorf("pack member %q: %s", file.Path, why)
	}
	member, err := root.Open(filepath.FromSlash(file.Path))
	if err != nil {
		return nil, err
	}
	defer func() { _ = member.Close() }()
	stat, err := member.Stat()
	if err != nil {
		return nil, err
	}
	if !stat.Mode().IsRegular() || stat.Size() != file.Size {
		return nil, fmt.Errorf("pack member %q no longer matches its manifest size or type", file.Path)
	}
	data, err := io.ReadAll(io.LimitReader(member, file.Size+1))
	if err != nil {
		return nil, err
	}
	sha := sha256.Sum256(data)
	b3 := blake3.Sum256(data)
	if int64(len(data)) != file.Size || (file.Sha256 == "" && file.Blake3 == "") ||
		(file.Sha256 != "" && !strings.EqualFold(hex.EncodeToString(sha[:]), file.Sha256)) ||
		(file.Blake3 != "" && !strings.EqualFold(hex.EncodeToString(b3[:]), file.Blake3)) {
		return nil, fmt.Errorf("pack member %q no longer matches its manifest digest", file.Path)
	}
	return data, nil
}
