package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestMutatingHandlersRejectGET(t *testing.T) {
	cases := []struct {
		name string
		h    http.HandlerFunc
		url  string
	}{
		{name: "activate", h: handleActivate, url: "http://example.com/admin/activate?gameId=launcher&version=1.0.0"},
		{name: "deleteVersion", h: handleDeleteVersion, url: "http://example.com/admin/deleteVersion?gameId=launcher&version=1.0.0"},
		{name: "upload", h: handleUpload, url: "http://example.com/admin/upload"},
		{name: "uploadStream", h: handleUploadStream, url: "http://example.com/admin/uploadStream"},

		{name: "feedbackDelete", h: handleFeedbackDelete, url: "http://example.com/admin/feedback/delete?id=1"},
		{name: "feedbackToggleImportant", h: handleFeedbackToggleImportant, url: "http://example.com/admin/feedback/toggleImportant?id=1"},
		{name: "feedbackMarkRead", h: handleFeedbackMarkRead, url: "http://example.com/admin/feedback/markRead?id=1"},
		{name: "feedbackMarkUnread", h: handleFeedbackMarkUnread, url: "http://example.com/admin/feedback/markUnread?id=1"},
		{name: "feedbackClear", h: handleFeedbackClear, url: "http://example.com/admin/feedback/clear"},

		{name: "gamesSave", h: handleGamesSave, url: "http://example.com/admin/games/save"},
		{name: "gameIconUpload", h: handleGameIconUpload, url: "http://example.com/admin/games/icon/upload?gameId=test"},

		{name: "newsRebuild", h: handleNewsRebuild, url: "http://example.com/admin/news/rebuild?scope=global"},
		{name: "newsSave", h: handleNewsSave, url: "http://example.com/admin/news/save"},
		{name: "newsDelete", h: handleNewsDelete, url: "http://example.com/admin/news/delete?scope=global&slug=test"},
		{name: "newsPublish", h: handleNewsPublish, url: "http://example.com/admin/news/publish"},
		{name: "newsPreview", h: handleNewsPreview, url: "http://example.com/admin/news/preview"},
		{name: "newsUploadCover", h: handleNewsUploadCover, url: "http://example.com/admin/news/uploadCover"},

		{name: "newsAssetsMkdir", h: handleNewsAssetsMkdir, url: "http://example.com/admin/news/assets/mkdir"},
		{name: "newsAssetsUpload", h: handleNewsAssetsUpload, url: "http://example.com/admin/news/assets/upload"},
		{name: "newsAssetsUploadByURL", h: handleNewsAssetsUploadByURL, url: "http://example.com/admin/news/assets/uploadByUrl"},
		{name: "newsAssetsDelete", h: handleNewsAssetsDelete, url: "http://example.com/admin/news/assets/delete"},
		{name: "newsAssetsRename", h: handleNewsAssetsRename, url: "http://example.com/admin/news/assets/rename"},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, tc.url, nil)
			w := httptest.NewRecorder()
			tc.h(w, req)
			if w.Code != http.StatusMethodNotAllowed {
				t.Fatalf("expected %d, got %d", http.StatusMethodNotAllowed, w.Code)
			}
		})
	}
}
