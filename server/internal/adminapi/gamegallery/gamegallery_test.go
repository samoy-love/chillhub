package gamegallery

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

// newHandlers points a handler set at a throwaway content root for one test.
func newHandlers(t *testing.T) (*Handlers, string) {
	t.Helper()
	dir := t.TempDir()
	return New(dir), dir
}

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
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, rawURL, &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// urlencodedForm builds an application/x-www-form-urlencoded POST request.
func urlencodedForm(t *testing.T, rawURL string, values url.Values) *http.Request {
	t.Helper()
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, rawURL, strings.NewReader(values.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	return req
}

// list calls List and returns the decoded listing.
func list(t *testing.T, h *Handlers, gid, path string) (int, map[string]any) {
	t.Helper()
	w := httptest.NewRecorder()
	h.List(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet,
		"http://example.com/admin/api/games/gallery/list?gameId="+url.QueryEscape(gid)+"&path="+url.QueryEscape(path), nil))
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

// The gallery browser round-trips: mkdir, upload, list, rename, delete — each
// step must actually take effect, or the admin panel shows a stale list and
// inserts URLs that 404 in the launcher.
func TestGalleryRoundTrip(t *testing.T) {
	h, root := newHandlers(t)

	w := httptest.NewRecorder()
	h.Mkdir(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/mkdir", url.Values{
		"gameId": {"my-game"}, "path": {""}, "name": {"screens"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("mkdir: %d %s", w.Code, w.Body.String())
	}

	w = httptest.NewRecorder()
	h.Upload(w, imageUpload(t, "http://example.com/admin/api/games/gallery/upload", "shot.png", map[string]string{
		"gameId": "my-game", "path": "screens", "filename": "shot",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("upload: %d %s", w.Code, w.Body.String())
	}
	var up map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &up); err != nil {
		t.Fatal(err)
	}
	if up["url"] != "/content/my-game/gallery/screens/"+up["filename"] {
		t.Fatalf("url = %q, filename = %q", up["url"], up["filename"])
	}
	if _, err := os.Stat(filepath.Join(root, "content", "my-game", "gallery", "screens", up["filename"])); err != nil {
		t.Fatalf("the uploaded picture is not on disk: %v", err)
	}

	code, listing := list(t, h, "my-game", "screens")
	if code != http.StatusOK {
		t.Fatalf("list: %d", code)
	}
	if got := names(listing); len(got) != 1 || got[0] != up["filename"] {
		t.Fatalf("listing = %v, want [%s]", got, up["filename"])
	}

	w = httptest.NewRecorder()
	h.Rename(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/rename", url.Values{
		"gameId": {"my-game"}, "path": {"screens"}, "from": {up["filename"]}, "to": {"renamed.jpg"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("rename: %d %s", w.Code, w.Body.String())
	}
	if _, listing := list(t, h, "my-game", "screens"); names(listing)[0] != "renamed.jpg" {
		t.Fatalf("rename did not take effect: %v", names(listing))
	}

	w = httptest.NewRecorder()
	h.Delete(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", url.Values{
		"gameId": {"my-game"}, "path": {"screens"}, "name": {"renamed.jpg"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", w.Code, w.Body.String())
	}
	if _, listing := list(t, h, "my-game", "screens"); len(names(listing)) != 0 {
		t.Fatalf("the picture survived deletion: %v", names(listing))
	}
}

// Every gallery mutation is per-game, so a bad or missing gameId must be
// refused before anything is created or touched on disk.
func TestGalleryRejectsUnsafeGameID(t *testing.T) {
	h, root := newHandlers(t)
	for _, gid := range []string{"../evil", "a/b", `..\evil`, "", "game id"} {
		w := httptest.NewRecorder()
		h.Mkdir(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/mkdir", url.Values{
			"gameId": {gid}, "path": {""}, "name": {"x"},
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("mkdir gameId %q: got %d, want 400", gid, w.Code)
		}

		w = httptest.NewRecorder()
		h.List(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet,
			"http://example.com/admin/api/games/gallery/list?gameId="+url.QueryEscape(gid), nil))
		if w.Code != http.StatusBadRequest {
			t.Errorf("list gameId %q: got %d, want 400", gid, w.Code)
		}

		w = httptest.NewRecorder()
		h.Upload(w, imageUpload(t, "http://example.com/admin/api/games/gallery/upload", "shot.png", map[string]string{
			"gameId": gid, "path": "", "filename": "shot",
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("upload gameId %q: got %d, want 400", gid, w.Code)
		}

		w = httptest.NewRecorder()
		h.Delete(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", url.Values{
			"gameId": {gid}, "path": {""}, "name": {"x"},
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("delete gameId %q: got %d, want 400", gid, w.Code)
		}

		w = httptest.NewRecorder()
		h.Rename(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/rename", url.Values{
			"gameId": {gid}, "path": {""}, "from": {"x"}, "to": {"y"},
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("rename gameId %q: got %d, want 400", gid, w.Code)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "content", "evil")); err == nil {
		t.Fatal("an unsafe gameId created a directory outside the gallery tree")
	}
}

// The gallery browser must not become a directory listing for the whole
// server: gameId and path both come from the admin panel's own UI state, but
// the same guard the news gallery relies on must hold here too.
func TestGalleryListCannotEscapeItsRoot(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "content", "my-game", "gallery")
	if err := os.MkdirAll(base, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "content", "my-game", "secret.json"), []byte("{}"), 0o600); err != nil {
		t.Fatal(err)
	}
	for _, p := range []string{"..", "../..", "../../..", "/", "//", "sub/../..", "....//"} {
		code, listing := list(t, h, "my-game", p)
		if code == http.StatusOK {
			for _, n := range names(listing) {
				if n == "secret.json" {
					t.Errorf("path %q listed outside the gallery: %v", p, names(listing))
				}
			}
		}
	}
}

// Deleting or renaming the gallery root itself would take out every
// screenshot for the game in one request, with no undo.
func TestGalleryCannotWipeOrMoveItsRoot(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "content", "my-game", "gallery")
	if err := os.MkdirAll(base, 0o750); err != nil {
		t.Fatal(err)
	}
	keep := filepath.Join(base, "keep.jpg")
	if err := os.WriteFile(keep, []byte("picture"), 0o600); err != nil {
		t.Fatal(err)
	}

	for _, v := range []url.Values{
		{"gameId": {"my-game"}, "path": {""}, "name": {"."}},
		{"gameId": {"my-game"}, "path": {""}, "name": {".."}},
	} {
		w := httptest.NewRecorder()
		h.Delete(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", v))
		if w.Code != http.StatusBadRequest {
			t.Errorf("delete %v: got %d, want 400", v, w.Code)
		}
	}
	for _, v := range []url.Values{
		{"gameId": {"my-game"}, "path": {""}, "from": {"."}, "to": {"elsewhere"}},
		{"gameId": {"my-game"}, "path": {""}, "from": {"keep.jpg"}, "to": {"."}},
	} {
		w := httptest.NewRecorder()
		h.Rename(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/rename", v))
		if w.Code != http.StatusBadRequest {
			t.Errorf("rename %v: got %d, want 400", v, w.Code)
		}
	}
	if _, err := os.Stat(keep); err != nil {
		t.Fatalf("the gallery root was wiped or moved: %v", err)
	}
}

// SetCover and SetCaption round-trip through gallery.json, and creating it on
// first use must not require a prior mkdir/upload.
func TestSetCoverAndSetCaptionRoundTrip(t *testing.T) {
	h, root := newHandlers(t)

	if err := h.SetCover("my-game", "cover.jpg"); err != nil {
		t.Fatalf("SetCover: %v", err)
	}
	if err := h.SetCaption("my-game", "cover.jpg", "обложка"); err != nil {
		t.Fatalf("SetCaption: %v", err)
	}
	// A second file, captioned before it has any other record — SetCaption must
	// append it rather than fail.
	if err := h.SetCaption("my-game", "extra.jpg", "ещё один кадр"); err != nil {
		t.Fatalf("SetCaption (new item): %v", err)
	}

	b, err := os.ReadFile(filepath.Join(root, "content", "my-game", "gallery", "gallery.json"))
	if err != nil {
		t.Fatalf("gallery.json not written: %v", err)
	}
	var gf galleryFile
	if err := json.Unmarshal(b, &gf); err != nil {
		t.Fatal(err)
	}
	if gf.Cover != "cover.jpg" {
		t.Fatalf("cover = %q, want cover.jpg", gf.Cover)
	}
	if len(gf.Items) != 2 {
		t.Fatalf("items = %+v, want 2 entries", gf.Items)
	}
	if gf.Items[0].File != "cover.jpg" || gf.Items[0].Caption != "обложка" {
		t.Fatalf("item 0 = %+v", gf.Items[0])
	}
	if gf.Items[1].File != "extra.jpg" || gf.Items[1].Caption != "ещё один кадр" {
		t.Fatalf("item 1 = %+v", gf.Items[1])
	}

	// Updating an existing item's caption must edit it in place, not duplicate it.
	if err := h.SetCaption("my-game", "cover.jpg", "новая подпись"); err != nil {
		t.Fatalf("SetCaption (update): %v", err)
	}
	b, err = os.ReadFile(filepath.Join(root, "content", "my-game", "gallery", "gallery.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(b, &gf); err != nil {
		t.Fatal(err)
	}
	if len(gf.Items) != 2 {
		t.Fatalf("caption update duplicated the item: %+v", gf.Items)
	}
	if gf.Items[0].Caption != "новая подпись" {
		t.Fatalf("caption not updated: %+v", gf.Items[0])
	}
}

// SetCover/SetCaption must refuse a traversal gameId or an empty file before
// touching the disk.
func TestSetCoverAndSetCaptionRejectBadInput(t *testing.T) {
	h, _ := newHandlers(t)
	for _, gid := range []string{"../evil", "a/b", ""} {
		if err := h.SetCover(gid, "cover.jpg"); err == nil {
			t.Errorf("SetCover accepted gameId %q", gid)
		}
		if err := h.SetCaption(gid, "cover.jpg", "x"); err == nil {
			t.Errorf("SetCaption accepted gameId %q", gid)
		}
	}
	if err := h.SetCover("my-game", ""); err == nil {
		t.Error("SetCover accepted an empty file name")
	}
	if err := h.SetCaption("my-game", "   ", "x"); err == nil {
		t.Error("SetCaption accepted an empty file name")
	}
}

// The HTTP entry points must reject GET: both mutate gallery.json.
func TestGalleryWriteEndpointsRejectGet(t *testing.T) {
	h, _ := newHandlers(t)
	for name, handler := range map[string]http.HandlerFunc{
		"mkdir":       h.Mkdir,
		"upload":      h.Upload,
		"uploadByUrl": h.UploadByURL,
		"delete":      h.Delete,
		"rename":      h.Rename,
		"setCover":    h.SetCoverHandler,
		"setCaption":  h.SetCaptionHandler,
	} {
		w := httptest.NewRecorder()
		handler(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/api/games/gallery/"+name, nil))
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s answered GET with %d, want 405", name, w.Code)
		}
	}
}

// SetCoverHandler/SetCaptionHandler must round-trip through HTTP too.
func TestSetCoverAndSetCaptionHandlers(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.SetCoverHandler(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/setCover", url.Values{
		"gameId": {"my-game"}, "file": {"cover.jpg"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("setCover: %d %s", w.Code, w.Body.String())
	}

	w = httptest.NewRecorder()
	h.SetCaptionHandler(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/setCaption", url.Values{
		"gameId": {"my-game"}, "file": {"cover.jpg"}, "caption": {"обложка"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("setCaption: %d %s", w.Code, w.Body.String())
	}

	w = httptest.NewRecorder()
	h.SetCaptionHandler(w, urlencodedForm(t, "http://example.com/admin/api/games/gallery/setCaption", url.Values{
		"gameId": {"../evil"}, "file": {"cover.jpg"}, "caption": {"x"},
	}))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("setCaption with an unsafe gameId: got %d, want 400", w.Code)
	}
}

// readGallery loads the manifest a test just made the handlers write.
func readGallery(t *testing.T, root, gid string) galleryFile {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, "content", gid, "gallery", "gallery.json"))
	if err != nil {
		t.Fatalf("gallery.json not readable: %v", err)
	}
	var gf galleryFile
	if err := json.Unmarshal(b, &gf); err != nil {
		t.Fatal(err)
	}
	return gf
}

// The admin flow is "upload a picture, press «Сделать обложкой»" — no caption
// anywhere. That has to be enough for the launcher, which builds its carousel
// from items and would treat a cover-only manifest as an empty gallery.
func TestSetCoverRegistersTheItem(t *testing.T) {
	h, root := newHandlers(t)

	if err := h.SetCover("my-game", "shot.png"); err != nil {
		t.Fatalf("SetCover: %v", err)
	}
	gf := readGallery(t, root, "my-game")
	if gf.Cover != "shot.png" {
		t.Fatalf("cover = %q, want shot.png", gf.Cover)
	}
	if len(gf.Items) != 1 || gf.Items[0].File != "shot.png" {
		t.Fatalf("cover was not registered in items: %+v", gf.Items)
	}

	// Making the same picture the cover twice must not duplicate its entry, and
	// must not wipe a caption it already had.
	if err := h.SetCaption("my-game", "shot.png", "подпись"); err != nil {
		t.Fatalf("SetCaption: %v", err)
	}
	if err := h.SetCover("my-game", "shot.png"); err != nil {
		t.Fatalf("SetCover (again): %v", err)
	}
	gf = readGallery(t, root, "my-game")
	if len(gf.Items) != 1 {
		t.Fatalf("repeat SetCover duplicated the item: %+v", gf.Items)
	}
	if gf.Items[0].Caption != "подпись" {
		t.Fatalf("repeat SetCover dropped the caption: %+v", gf.Items[0])
	}
}

// The gallery browser can create and enter subdirectories, so a picture inside
// one must be usable as a cover. SanitizeFilename alone folded the separator
// into '_' and recorded a name that does not exist.
func TestSetCoverKeepsSubdirectoryPath(t *testing.T) {
	h, root := newHandlers(t)

	if err := h.SetCover("my-game", "shots/moon.png"); err != nil {
		t.Fatalf("SetCover: %v", err)
	}
	if err := h.SetCaption("my-game", "shots/moon.png", "луна"); err != nil {
		t.Fatalf("SetCaption: %v", err)
	}
	gf := readGallery(t, root, "my-game")
	if gf.Cover != "shots/moon.png" {
		t.Fatalf("cover = %q, want shots/moon.png", gf.Cover)
	}
	if len(gf.Items) != 1 || gf.Items[0].File != "shots/moon.png" || gf.Items[0].Caption != "луна" {
		t.Fatalf("subdirectory item mangled: %+v", gf.Items)
	}
}

// A reference must not climb out of the gallery even when it arrives as a path.
func TestSetCoverRejectsTraversalPath(t *testing.T) {
	h, root := newHandlers(t)
	if err := h.SetCover("my-game", "../../secrets.png"); err != nil {
		t.Fatalf("SetCover: %v", err)
	}
	gf := readGallery(t, root, "my-game")
	if strings.Contains(gf.Cover, "..") {
		t.Fatalf("traversal survived sanitising: cover = %q", gf.Cover)
	}
}

// Deleting a picture has to take its manifest entry with it: a cover left
// pointing at a deleted file is a 404 on the launcher's витрина.
func TestDeleteForgetsTheManifestEntry(t *testing.T) {
	h, root := newHandlers(t)
	dir := filepath.Join(root, "content", "my-game", "gallery")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "shot.png"), smallPNG(t), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := h.SetCover("my-game", "shot.png"); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	h.Delete(rec, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", url.Values{
		"gameId": {"my-game"}, "path": {""}, "name": {"shot.png"},
	}))
	if rec.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", rec.Code, rec.Body.String())
	}

	gf := readGallery(t, root, "my-game")
	if gf.Cover != "" {
		t.Fatalf("cover still points at the deleted file: %q", gf.Cover)
	}
	if len(gf.Items) != 0 {
		t.Fatalf("items still list the deleted file: %+v", gf.Items)
	}
}

// Renaming has to carry the manifest entry over, caption and all.
func TestRenameMovesTheManifestEntry(t *testing.T) {
	h, root := newHandlers(t)
	dir := filepath.Join(root, "content", "my-game", "gallery")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "old.png"), smallPNG(t), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := h.SetCover("my-game", "old.png"); err != nil {
		t.Fatal(err)
	}
	if err := h.SetCaption("my-game", "old.png", "кадр"); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	h.Rename(rec, urlencodedForm(t, "http://example.com/admin/api/games/gallery/rename", url.Values{
		"gameId": {"my-game"}, "path": {""}, "from": {"old.png"}, "to": {"new.png"},
	}))
	if rec.Code != http.StatusOK {
		t.Fatalf("rename: %d %s", rec.Code, rec.Body.String())
	}

	gf := readGallery(t, root, "my-game")
	if gf.Cover != "new.png" {
		t.Fatalf("cover = %q, want new.png", gf.Cover)
	}
	if len(gf.Items) != 1 || gf.Items[0].File != "new.png" || gf.Items[0].Caption != "кадр" {
		t.Fatalf("item did not follow the rename: %+v", gf.Items)
	}
}

// A directory carries every picture under it through delete and rename.
func TestDirectoryDeleteAndRenameCarryTheirPictures(t *testing.T) {
	h, root := newHandlers(t)
	dir := filepath.Join(root, "content", "my-game", "gallery", "shots")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "moon.png"), smallPNG(t), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := h.SetCover("my-game", "shots/moon.png"); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	h.Rename(rec, urlencodedForm(t, "http://example.com/admin/api/games/gallery/rename", url.Values{
		"gameId": {"my-game"}, "path": {""}, "from": {"shots"}, "to": {"screens"},
	}))
	if rec.Code != http.StatusOK {
		t.Fatalf("rename dir: %d %s", rec.Code, rec.Body.String())
	}
	if gf := readGallery(t, root, "my-game"); gf.Cover != "screens/moon.png" {
		t.Fatalf("cover = %q, want screens/moon.png", gf.Cover)
	}

	rec = httptest.NewRecorder()
	h.Delete(rec, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", url.Values{
		"gameId": {"my-game"}, "path": {""}, "name": {"screens"},
	}))
	if rec.Code != http.StatusOK {
		t.Fatalf("delete dir: %d %s", rec.Code, rec.Body.String())
	}
	gf := readGallery(t, root, "my-game")
	if gf.Cover != "" || len(gf.Items) != 0 {
		t.Fatalf("directory delete left orphans: %+v", gf)
	}
}

// A game whose gallery was never captioned has no gallery.json. Deleting a
// picture from it must not conjure an empty manifest into existence.
func TestDeleteWithoutManifestWritesNothing(t *testing.T) {
	h, root := newHandlers(t)
	dir := filepath.Join(root, "content", "my-game", "gallery")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "shot.png"), smallPNG(t), 0o644); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	h.Delete(rec, urlencodedForm(t, "http://example.com/admin/api/games/gallery/delete", url.Values{
		"gameId": {"my-game"}, "path": {""}, "name": {"shot.png"},
	}))
	if rec.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(dir, "gallery.json")); !os.IsNotExist(err) {
		t.Fatal("delete created a gallery.json for a gallery that had none")
	}
}
