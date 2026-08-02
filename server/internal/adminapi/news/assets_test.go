package news

import (
	"bytes"
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// smallPNG is a real, decodable image: the upload pipeline re-encodes what it
// receives, so a placeholder byte slice would never reach the code under test.
func smallPNG(t *testing.T) []byte {
	t.Helper()
	img := image.NewRGBA(image.Rect(0, 0, 8, 8))
	img.Set(0, 0, color.RGBA{R: 200, A: 255})
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// imageUpload builds a multipart POST with one real PNG in the "file" part.
func imageUpload(t *testing.T, rawURL, filename string, fields map[string]string) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	for k, v := range fields {
		if err := mw.WriteField(k, v); err != nil {
			t.Fatal(err)
		}
	}
	fw, err := mw.CreateFormFile("file", filename)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(smallPNG(t)); err != nil {
		t.Fatal(err)
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, rawURL, &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// coverUpload is the cover-image variant used by the article tests.
func coverUpload(t *testing.T, filename string, fields map[string]string) *http.Request {
	t.Helper()
	return imageUpload(t, "http://example.com/admin/api/news/uploadCover", filename, fields)
}

// assetsList calls AssetsList and returns the decoded listing.
func assetsList(t *testing.T, h *Handlers, path string) (int, map[string]any) {
	t.Helper()
	w := httptest.NewRecorder()
	h.AssetsList(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/news/assets/list?path="+url.QueryEscape(path), nil))
	var out map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &out)
	return w.Code, out
}

// names returns the entry names of a decoded listing.
func names(listing map[string]any) []string {
	var out []string
	items, _ := listing["items"].([]any)
	for _, it := range items {
		m, _ := it.(map[string]any)
		if s, ok := m["name"].(string); ok {
			out = append(out, s)
		}
	}
	return out
}

// The gallery is what the editor inserts pictures from. Create a folder, put an
// image in it, rename it, delete it — if any step silently no-ops, the admin
// sees a stale file list and inserts URLs that 404 in the launcher.
func TestAssetsGalleryRoundTrip(t *testing.T) {
	h, root := newHandlers(t)

	w := httptest.NewRecorder()
	h.AssetsMkdir(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/mkdir", url.Values{
		"path": {""}, "name": {"patch-notes"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("mkdir: %d %s", w.Code, w.Body.String())
	}

	w = httptest.NewRecorder()
	h.AssetsUpload(w, imageUpload(t, "http://example.com/admin/api/news/assets/upload", "shot.png", map[string]string{
		"path": "patch-notes", "filename": "shot",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("upload: %d %s", w.Code, w.Body.String())
	}
	var up map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &up); err != nil {
		t.Fatal(err)
	}
	// The URL handed to the editor must be the public /assets/ path, including
	// the subdirectory: without it the picture resolves to the gallery root.
	if up["url"] != "/assets/patch-notes/"+up["filename"] {
		t.Fatalf("url = %q, filename = %q", up["url"], up["filename"])
	}
	if _, err := os.Stat(filepath.Join(root, "news", "assets", "patch-notes", up["filename"])); err != nil {
		t.Fatalf("the uploaded asset is not on disk: %v", err)
	}

	code, listing := assetsList(t, h, "patch-notes")
	if code != http.StatusOK {
		t.Fatalf("list: %d", code)
	}
	if got := names(listing); len(got) != 1 || got[0] != up["filename"] {
		t.Fatalf("listing = %v, want [%s]", got, up["filename"])
	}

	w = httptest.NewRecorder()
	h.AssetsRename(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/rename", url.Values{
		"path": {"patch-notes"}, "from": {up["filename"]}, "to": {"renamed.jpg"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("rename: %d %s", w.Code, w.Body.String())
	}
	if _, listing := assetsList(t, h, "patch-notes"); names(listing)[0] != "renamed.jpg" {
		t.Fatalf("rename did not take effect: %v", names(listing))
	}

	w = httptest.NewRecorder()
	h.AssetsDelete(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/delete", url.Values{
		"path": {"patch-notes"}, "name": {"renamed.jpg"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", w.Code, w.Body.String())
	}
	if _, listing := assetsList(t, h, "patch-notes"); len(names(listing)) != 0 {
		t.Fatalf("the file survived deletion: %v", names(listing))
	}
}

// The gallery browser must not become a directory listing for the whole server.
// Its path parameter comes from the URL bar of the admin panel, which is the
// first thing anybody with a session tries to walk out of.
func TestAssetsListCannotEscapeTheGallery(t *testing.T) {
	h, root := newHandlers(t)
	if err := os.MkdirAll(filepath.Join(root, "news", "assets"), 0o755); err != nil {
		t.Fatal(err)
	}
	// Something recognisable outside the gallery but inside the content root.
	if err := os.WriteFile(filepath.Join(root, "news", "index.json"), []byte("{}"), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, p := range []string{"..", "../..", "../../..", "/", "//", "sub/../..", "....//"} {
		code, listing := assetsList(t, h, p)
		if code == http.StatusOK {
			for _, n := range names(listing) {
				if n == "index.json" || n == "news_private" {
					t.Errorf("path %q listed the news tree: %v", p, names(listing))
				}
			}
		}
	}
}

// Nothing an admin can type into the "delete" dialog may take out the gallery
// itself. Every news cover, every landing-page picture and every game icon
// lives under this one directory and there is no undo.
func TestAssetsDeleteCannotWipeTheGalleryRoot(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "news", "assets")
	if err := os.MkdirAll(base, 0o755); err != nil {
		t.Fatal(err)
	}
	keep := filepath.Join(base, "keep.jpg")
	if err := os.WriteFile(keep, []byte("picture"), 0o644); err != nil {
		t.Fatal(err)
	}

	for _, v := range []url.Values{
		{"path": {""}, "name": {"."}},
		{"path": {""}, "name": {".."}},
		{"path": {"."}, "name": {"."}},
		{"path": {"sub"}, "name": {".."}},
	} {
		w := httptest.NewRecorder()
		h.AssetsDelete(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/delete", v))
		if w.Code != http.StatusBadRequest {
			t.Errorf("%v: got %d, want 400", v, w.Code)
		}
		if _, err := os.Stat(keep); err != nil {
			t.Fatalf("%v: the gallery was wiped: %v", v, err)
		}
	}
}

// The same for rename: renaming the gallery root away is indistinguishable from
// deleting it, and every stored /assets/ URL breaks at once.
func TestAssetsRenameCannotMoveTheGalleryRoot(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "news", "assets")
	if err := os.MkdirAll(base, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, v := range []url.Values{
		{"path": {""}, "from": {"."}, "to": {"elsewhere"}},
		{"path": {""}, "from": {"keep.jpg"}, "to": {"."}},
		{"path": {""}, "from": {"keep.jpg"}, "to": {".."}},
		{"path": {""}, "from": {".."}, "to": {"x"}},
	} {
		w := httptest.NewRecorder()
		h.AssetsRename(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/rename", v))
		if w.Code != http.StatusBadRequest {
			t.Errorf("%v: got %d, want 400", v, w.Code)
		}
		if _, err := os.Stat(base); err != nil {
			t.Fatalf("%v: the gallery root moved: %v", v, err)
		}
	}
}

// A folder name is one path segment. A name carrying separators must be
// flattened into the gallery rather than creating a directory next to it — the
// listing can only navigate inside the gallery, so anything created outside is
// both invisible and, being under content/news, publicly served.
func TestAssetsMkdirCannotCreateFoldersOutsideTheGallery(t *testing.T) {
	h, root := newHandlers(t)
	for _, name := range []string{"../escaped", "..\\escaped", "sub/deep", "a/../../b"} {
		w := httptest.NewRecorder()
		h.AssetsMkdir(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/mkdir", url.Values{
			"path": {""}, "name": {name},
		}))
		if w.Code != http.StatusOK {
			t.Fatalf("mkdir %q: %d %s", name, w.Code, w.Body.String())
		}
		for _, outside := range []string{"escaped", "b", "deep"} {
			if _, err := os.Stat(filepath.Join(root, "news", outside)); err == nil {
				t.Errorf("name %q created %q outside the gallery", name, outside)
			}
		}
	}
	// Everything landed as a flat directory inside the gallery.
	_, listing := assetsList(t, h, "")
	for _, n := range names(listing) {
		if strings.ContainsAny(n, `/\`) {
			t.Errorf("a folder name kept a path separator: %q", n)
		}
	}
}

// Downloading a cover by URL is a server-side fetch driven by admin input. It
// must refuse the empty case and anything pointing back at the machine itself,
// or the admin panel becomes a port scanner for the internal network.
func TestAssetsUploadByURLRefusesEmptyAndInternalTargets(t *testing.T) {
	h, _ := newHandlers(t)
	for _, u := range []string{"", "   ", "http://127.0.0.1:8080/x.png", "http://localhost/x.png", "file:///etc/passwd", "http://169.254.169.254/latest/meta-data"} {
		w := httptest.NewRecorder()
		h.AssetsUploadByURL(w, urlencodedForm(t, "http://example.com/admin/api/news/assets/uploadByUrl", url.Values{
			"path": {""}, "url": {u},
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("url %q: got %d, want 400", u, w.Code)
		}
	}
}

// A missing file part must be a 400 rather than a 500 with a filesystem path in
// the body — the upload endpoints are reachable without nginx's auth_request.
func TestAssetsUploadWithoutAFileIsABadRequest(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.AssetsUpload(w, multipartForm(t, "http://example.com/admin/api/news/assets/upload", map[string]string{
		"path": "", "filename": "x",
	}))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", w.Code)
	}

	w = httptest.NewRecorder()
	h.UploadCover(w, multipartForm(t, "http://example.com/admin/api/news/uploadCover", map[string]string{
		"scope": "launcher",
	}))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("uploadCover without a file: got %d, want 400", w.Code)
	}
}

// Every gallery mutation is state-changing, so GET must not reach it.
func TestAssetsWriteEndpointsRejectGet(t *testing.T) {
	h, _ := newHandlers(t)
	for name, handler := range map[string]http.HandlerFunc{
		"mkdir":       h.AssetsMkdir,
		"upload":      h.AssetsUpload,
		"uploadByUrl": h.AssetsUploadByURL,
		"delete":      h.AssetsDelete,
		"rename":      h.AssetsRename,
	} {
		w := httptest.NewRecorder()
		handler(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/news/assets/"+name, nil))
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s answered GET with %d, want 405", name, w.Code)
		}
	}
}

// Listing a directory that is not there must not echo the absolute content root
// back to the caller: it is the deployment layout, and these endpoints answer
// before nginx's auth_request on some routes.
func TestAssetsListHidesTheContentRootOnError(t *testing.T) {
	h, root := newHandlers(t)
	w := httptest.NewRecorder()
	h.AssetsList(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/news/assets/list?path=nope", nil))
	if w.Code != http.StatusNotFound {
		t.Fatalf("got %d, want 404", w.Code)
	}
	if strings.Contains(w.Body.String(), root) || strings.Contains(w.Body.String(), "news") {
		t.Errorf("the error body leaks the content path: %s", w.Body.String())
	}
}

// The gallery browser is also used as a folder picker, where only directories
// may show up, and as a search box.
func TestAssetsListFiltersByNameAndDirsOnly(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "news", "assets")
	if err := os.MkdirAll(filepath.Join(base, "screens"), 0o755); err != nil {
		t.Fatal(err)
	}
	for _, n := range []string{"alpha.jpg", "beta.jpg"} {
		if err := os.WriteFile(filepath.Join(base, n), []byte("x"), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	w := httptest.NewRecorder()
	h.AssetsList(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/news/assets/list?dirsOnly=1", nil))
	var listing map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &listing); err != nil {
		t.Fatal(err)
	}
	if got := names(listing); len(got) != 1 || got[0] != "screens" {
		t.Errorf("dirsOnly returned files too: %v", got)
	}

	w = httptest.NewRecorder()
	h.AssetsList(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/news/assets/list?q=ALPHA", nil))
	if err := json.Unmarshal(w.Body.Bytes(), &listing); err != nil {
		t.Fatal(err)
	}
	if got := names(listing); len(got) != 1 || got[0] != "alpha.jpg" {
		t.Errorf("the search is not case-insensitive: %v", got)
	}
}
