package media

import (
	"bytes"
	"encoding/binary"
	"hash/crc32"
	"image"
	"image/color"
	"image/png"
	"testing"
)

// pngBomb builds a valid PNG header that DECLARES huge dimensions while the
// file itself stays a few hundred bytes — the classic decompression bomb.
func pngBomb(t *testing.T, w, h uint32) []byte {
	t.Helper()
	var buf bytes.Buffer
	buf.Write([]byte{0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a})

	var ihdr bytes.Buffer
	_ = binary.Write(&ihdr, binary.BigEndian, w)
	_ = binary.Write(&ihdr, binary.BigEndian, h)
	ihdr.Write([]byte{8, 6, 0, 0, 0}) // bit depth 8, RGBA, no interlace

	chunk := func(typ string, data []byte) {
		_ = binary.Write(&buf, binary.BigEndian, uint32(len(data)))
		buf.WriteString(typ)
		buf.Write(data)
		c := crc32.NewIEEE()
		c.Write([]byte(typ))
		c.Write(data)
		_ = binary.Write(&buf, binary.BigEndian, c.Sum32())
	}
	chunk("IHDR", ihdr.Bytes())
	chunk("IDAT", []byte{0x78, 0x9c, 0x01, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01})
	chunk("IEND", nil)
	return buf.Bytes()
}

func smallPNG(t *testing.T, w, h int) []byte {
	t.Helper()
	img := image.NewRGBA(image.Rect(0, 0, w, h))
	img.Set(0, 0, color.RGBA{R: 9, A: 255})
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// A tiny file declaring 30000x30000 must be refused from its header, before any
// decoder allocates 3.6 GB for the pixel buffer.
func TestCheckImageBoundsRejectsBomb(t *testing.T) {
	if err := CheckImageBounds(pngBomb(t, 30000, 30000)); err == nil {
		t.Fatal("a 30000x30000 PNG was accepted")
	}
	if err := CheckImageBounds(pngBomb(t, 100000, 4)); err == nil {
		t.Fatal("a 100000px wide PNG was accepted")
	}
	// Just under the dimension cap but over the pixel cap.
	if err := CheckImageBounds(pngBomb(t, 7999, 7999)); err == nil {
		t.Fatal("a 63 megapixel PNG was accepted")
	}
}

func TestCheckImageBoundsAcceptsNormalImages(t *testing.T) {
	for _, dim := range [][2]int{{16, 16}, {1920, 1080}, {3000, 2000}} {
		if err := CheckImageBounds(smallPNG(t, dim[0], dim[1])); err != nil {
			t.Errorf("%dx%d rejected: %v", dim[0], dim[1], err)
		}
	}
}

// The asset pipeline must refuse the bomb rather than decode it.
func TestProcessAndSaveAssetRejectsBomb(t *testing.T) {
	base := t.TempDir()
	if _, _, err := ProcessAndSaveAsset(base, "", "bomb", pngBomb(t, 30000, 30000), ".png", ""); err == nil {
		t.Fatal("ProcessAndSaveAsset decoded a PNG bomb")
	}
}
