package media

import (
	"bytes"
	"encoding/binary"
	"hash/crc32"
	"image"
	"image/color"
	"image/png"
	"net"
	"net/http"
	"net/http/httptest"
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
		// #nosec G115 -- the chunks are literals built in this helper, a few
		// dozen bytes each; the length cannot come near the uint32 range.
		_ = binary.Write(&buf, binary.BigEndian, uint32(len(data)))
		buf.WriteString(typ)
		buf.Write(data)
		c := crc32.NewIEEE()
		_, _ = c.Write([]byte(typ))
		_, _ = c.Write(data)
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

// DownloadURL runs inside the server's network: it must not be usable as a
// probe of loopback, the LAN or the cloud metadata service.
func TestDownloadURLBlocksPrivateAddresses(t *testing.T) {
	blocked := []string{
		"http://127.0.0.1:55777/admin/api/health",
		"http://localhost:55777/admin/",
		"http://[::1]:8080/",
		"http://169.254.169.254/latest/meta-data/",
		"http://10.0.0.5/x.png",
		"http://192.168.1.1/x.png",
		"http://172.16.0.1/x.png",
		"http://100.64.0.1/x.png",
		"http://0.0.0.0/",
	}
	for _, u := range blocked {
		if _, _, err := DownloadURL(t.Context(), u); err == nil {
			t.Errorf("%s was fetched", u)
		}
	}
	// Non-HTTP schemes stay refused too.
	for _, u := range []string{"file:///etc/passwd", "gopher://x/", "ftp://example.com/x"} {
		if _, _, err := DownloadURL(t.Context(), u); err == nil {
			t.Errorf("%s was fetched", u)
		}
	}
}

// The same block must apply to a hostname that only resolves to loopback, and
// to a redirect that lands there.
func TestDownloadURLBlocksLoopbackServer(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte("secret"))
	}))
	defer srv.Close()
	if b, _, err := DownloadURL(t.Context(), srv.URL); err == nil {
		t.Fatalf("fetched a loopback server: %q", string(b))
	}

	redirector := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, srv.URL, http.StatusFound)
	}))
	defer redirector.Close()
	if _, _, err := DownloadURL(t.Context(), redirector.URL); err == nil {
		t.Fatal("followed a redirect into loopback")
	}
}

func TestBlockedIPClassification(t *testing.T) {
	for _, s := range []string{"127.0.0.1", "::1", "10.1.2.3", "192.168.0.1", "172.20.0.1",
		"169.254.169.254", "0.0.0.0", "fc00::1", "100.100.0.1", "224.0.0.1"} {
		if !blockedIP(net.ParseIP(s)) {
			t.Errorf("%s must be blocked", s)
		}
	}
	for _, s := range []string{"8.8.8.8", "1.1.1.1", "93.184.216.34", "2606:4700::1111"} {
		if blockedIP(net.ParseIP(s)) {
			t.Errorf("%s must be allowed", s)
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
