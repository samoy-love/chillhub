package games

import (
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/url"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

// Presentation is editorial information shown on a game's page, independent
// of announcements and the executable modpack manifest.
type Presentation struct {
	Description        string           `json:"description"`
	ModpackDescription string           `json:"modpackDescription"`
	ModpackVersion     string           `json:"modpackVersion,omitempty"`
	ModpackRevision    string           `json:"modpackRevision,omitempty"`
	Genre              string           `json:"genre"`
	HeroURL            string           `json:"heroUrl"`
	Highlights         []string         `json:"highlights,omitempty"`
	Media              []GameMedia      `json:"media,omitempty"`
	Mods               []ModDescription `json:"mods,omitempty"`
	ModBenefits        []ModBenefit     `json:"modBenefits,omitempty"`
}

type ModBenefit struct {
	Title       string `json:"title"`
	Description string `json:"description"`
	Icon        string `json:"icon"`
}

type GameMedia struct {
	Kind        string `json:"kind"`
	URL         string `json:"url"`
	PosterURL   string `json:"posterUrl"`
	Caption     string `json:"caption"`
	Provider    string `json:"provider,omitempty"`
	Role        string `json:"role,omitempty"`
	SourceURL   string `json:"sourceUrl,omitempty"`
	Author      string `json:"author,omitempty"`
	PublishedAt string `json:"publishedAt,omitempty"`
	Width       int    `json:"width,omitempty"`
	Height      int    `json:"height,omitempty"`
}

type ModDescription struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	RussianName string `json:"russianName"`
	Description string `json:"description"`
	Category    string `json:"category"`
	Version     string `json:"version"`
	URL         string `json:"url"`
}

// ValidatePresentation bounds both editor input and hand-edited registry data.
// It never fetches a URL; media is either an HTTPS resource or a local asset URL.
func ValidatePresentation(p *Presentation) error {
	if p == nil {
		return nil
	}
	for _, field := range []struct {
		name, text string
		limit      int
	}{
		{"description", p.Description, 12000}, {"genre", p.Genre, 120},
		{"modpackDescription", p.ModpackDescription, 2000},
		{"modpackVersion", p.ModpackVersion, 200},
	} {
		if err := presentationText(field.name, field.text, field.limit); err != nil {
			return err
		}
	}
	if p.ModpackRevision != "" {
		if decoded, err := hex.DecodeString(p.ModpackRevision); err != nil || len(decoded) != 16 {
			return errors.New("presentation.modpackRevision: нужны 32 шестнадцатеричных символа")
		}
	}
	if err := presentationURL("heroUrl", p.HeroURL, true); err != nil {
		return err
	}
	for _, group := range []struct {
		name          string
		values        []string
		count, length int
	}{
		{"highlights", p.Highlights, 24, 240},
	} {
		if len(group.values) > group.count {
			return fmt.Errorf("presentation.%s: максимум %d пунктов", group.name, group.count)
		}
		for i, text := range group.values {
			name := fmt.Sprintf("%s[%d]", group.name, i)
			if strings.TrimSpace(text) == "" {
				return fmt.Errorf("presentation.%s: пустой пункт", name)
			}
			if err := presentationText(name, text, group.length); err != nil {
				return err
			}
		}
	}
	if err := validatePresentationMedia(p.Media); err != nil {
		return err
	}
	if err := validateModBenefits(p.ModBenefits); err != nil {
		return err
	}
	return validatePresentationMods(p.Mods)
}

func validateModBenefits(benefits []ModBenefit) error {
	if len(benefits) > 5 {
		return errors.New("presentation.modBenefits: максимум 5 преимуществ")
	}
	for i, benefit := range benefits {
		prefix := fmt.Sprintf("modBenefits[%d]", i)
		if strings.TrimSpace(benefit.Title) == "" || strings.TrimSpace(benefit.Description) == "" {
			return fmt.Errorf("presentation.%s: укажите заголовок и описание", prefix)
		}
		if err := presentationText(prefix+".title", benefit.Title, 80); err != nil {
			return err
		}
		if err := presentationText(prefix+".description", benefit.Description, 240); err != nil {
			return err
		}
		switch benefit.Icon {
		case "package", "users", "gamepad", "sparkles", "wrench", "compass":
		default:
			return fmt.Errorf("presentation.%s.icon: неизвестный значок", prefix)
		}
	}
	return nil
}

func validatePresentationMedia(mediaItems []GameMedia) error {
	if len(mediaItems) > 24 {
		return errors.New("presentation.media: максимум 24 файла")
	}
	for i, media := range mediaItems {
		prefix := fmt.Sprintf("media[%d]", i)
		if media.Kind != "image" && media.Kind != "video" {
			return fmt.Errorf("presentation.%s.kind: выберите image или video", prefix)
		}
		if err := presentationURL(prefix+".url", media.URL, false); err != nil {
			return err
		}
		if err := presentationURL(prefix+".posterUrl", media.PosterURL, true); err != nil {
			return err
		}
		if err := presentationText(prefix+".caption", media.Caption, 240); err != nil {
			return err
		}
		if err := validateMediaSource(prefix, media); err != nil {
			return err
		}
	}
	return nil
}

func validateMediaSource(prefix string, media GameMedia) error {
	if err := presentationURL(prefix+".sourceUrl", media.SourceURL, true); err != nil {
		return err
	}
	if err := presentationText(prefix+".author", media.Author, 160); err != nil {
		return err
	}
	if err := validateMediaDimensionsAndDate(prefix, media); err != nil {
		return err
	}
	if media.Kind == "image" {
		if media.Provider != "" && media.Provider != "file" || media.Role != "" {
			return fmt.Errorf("presentation.%s: у скриншота не бывает видеопровайдера или роли", prefix)
		}
		return nil
	}
	if media.Role != "" && media.Role != "trailer" && media.Role != "gameplay" {
		return fmt.Errorf("presentation.%s.role: выберите trailer или gameplay", prefix)
	}
	return validateVideoProvider(prefix, media)
}

func validateMediaDimensionsAndDate(prefix string, media GameMedia) error {
	if media.Width < 0 || media.Width > 16384 || media.Height < 0 || media.Height > 16384 || (media.Width == 0) != (media.Height == 0) {
		return fmt.Errorf("presentation.%s: неверные размеры медиа", prefix)
	}
	if media.PublishedAt != "" {
		if _, err := time.Parse("2006-01-02", media.PublishedAt); err != nil {
			return fmt.Errorf("presentation.%s.publishedAt: нужна дата ГГГГ-ММ-ДД", prefix)
		}
	}
	return nil
}

func validateVideoProvider(prefix string, media GameMedia) error {
	switch media.Provider {
	case "", "file":
		return nil
	case "youtube", "rutube":
		u, err := url.Parse(media.URL)
		if err != nil || u.Port() != "" {
			return fmt.Errorf("presentation.%s.url: неверная ссылка видеопровайдера", prefix)
		}
		if externalVideoURL(media.Provider, u) {
			return nil
		}
		return fmt.Errorf("presentation.%s.url: ссылка не соответствует выбранному видеопровайдеру", prefix)
	default:
		return fmt.Errorf("presentation.%s.provider: выберите file, youtube или rutube", prefix)
	}
}

func externalVideoURL(provider string, u *url.URL) bool {
	host := strings.ToLower(u.Hostname())
	if provider == "youtube" {
		switch host {
		case "youtu.be":
			return validVideoID(strings.TrimPrefix(u.Path, "/"), 11)
		case "youtube.com", "www.youtube.com", "m.youtube.com":
			return u.Path == "/watch" && validVideoID(u.Query().Get("v"), 11)
		default:
			return false
		}
	}
	if host != "rutube.ru" && host != "www.rutube.ru" {
		return false
	}
	id := strings.TrimSuffix(strings.TrimPrefix(u.Path, "/video/"), "/")
	decoded, err := hex.DecodeString(id)
	return strings.HasPrefix(u.Path, "/video/") && err == nil && len(decoded) == 16
}

func validVideoID(id string, length int) bool {
	if len(id) != length {
		return false
	}
	for _, c := range id {
		if c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' {
			continue
		}
		return false
	}
	return true
}

func validatePresentationMods(mods []ModDescription) error {
	if len(mods) > 256 {
		return errors.New("presentation.mods: максимум 256 модов")
	}
	seen := make(map[string]bool, len(mods))
	for i, mod := range mods {
		prefix := fmt.Sprintf("mods[%d]", i)
		id := strings.ToLower(strings.TrimSpace(mod.ID))
		if id == "" || seen[id] {
			return fmt.Errorf("presentation.%s.id: нужен уникальный идентификатор", prefix)
		}
		seen[id] = true
		if strings.TrimSpace(mod.Name) == "" {
			return fmt.Errorf("presentation.%s.name: укажите название", prefix)
		}
		for _, field := range []struct {
			name, text string
			limit      int
		}{
			{"id", mod.ID, 160}, {"name", mod.Name, 160}, {"russianName", mod.RussianName, 160},
			{"description", mod.Description, 2000}, {"category", mod.Category, 80}, {"version", mod.Version, 80},
		} {
			if err := presentationText(prefix+"."+field.name, field.text, field.limit); err != nil {
				return err
			}
		}
		if err := presentationURL(prefix+".url", mod.URL, true); err != nil {
			return err
		}
	}
	return nil
}

func presentationText(field, value string, limit int) error {
	if !utf8.ValidString(value) || utf8.RuneCountInString(value) > limit {
		return fmt.Errorf("presentation.%s: максимум %d символов", field, limit)
	}
	for _, c := range value {
		if unicode.IsControl(c) && c != '\n' && c != '\r' && c != '\t' {
			return fmt.Errorf("presentation.%s: недопустимый символ", field)
		}
	}
	return nil
}

func presentationURL(field, value string, optional bool) error {
	bad := func() error {
		return fmt.Errorf("presentation.%s: нужна HTTPS-ссылка или путь /assets/…", field)
	}
	if value == "" && optional {
		return nil
	}
	if value == "" || len(value) > 2048 || strings.Contains(value, "\\") || strings.IndexFunc(value, unicode.IsSpace) >= 0 {
		return bad()
	}
	u, err := url.Parse(value)
	if err != nil || u.User != nil || u.Fragment != "" || u.Opaque != "" {
		return bad()
	}
	if !safePresentationPath(u.Path) {
		return bad()
	}
	if strings.HasPrefix(value, "/") {
		if localPresentationURL(u) {
			return nil
		}
		return bad()
	}
	if remotePresentationURL(u) {
		return nil
	}
	return bad()
}

func safePresentationPath(path string) bool {
	if strings.IndexFunc(path, unicode.IsControl) >= 0 {
		return false
	}
	for component := range strings.SplitSeq(path, "/") {
		if component == "." || component == ".." || strings.Contains(component, "\\") {
			return false
		}
	}
	return true
}

func localPresentationURL(u *url.URL) bool {
	if strings.HasPrefix(u.Path, "//") || u.Host != "" || u.Scheme != "" {
		return false
	}
	for _, prefix := range []string{"/assets/", "/gallery/", "/manifests/", "/content/"} {
		if strings.HasPrefix(u.Path, prefix) {
			return true
		}
	}
	return false
}

func remotePresentationURL(u *url.URL) bool {
	if u.Scheme != "https" || u.Hostname() == "" {
		return false
	}
	host := strings.ToLower(strings.TrimSuffix(u.Hostname(), "."))
	if host == "localhost" || strings.HasSuffix(host, ".localhost") || strings.HasSuffix(host, ".local") {
		return false
	}
	if ip := net.ParseIP(host); ip != nil && (ip.IsLoopback() || ip.IsPrivate() || ip.IsUnspecified() || ip.IsLinkLocalUnicast() || ip.IsMulticast()) {
		return false
	}
	return true
}

// PublicPresentation omits malformed editorial content without hiding a game.
func PublicPresentation(p *Presentation) *Presentation {
	if ValidatePresentation(p) != nil {
		return nil
	}
	return p
}
