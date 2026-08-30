package metrics

import "strings"

// Свои автоматические прогоны — это не пользователи.
//
// Установщик, лаунчер и деплой гоняются автотестами и отладочными запусками, и
// каждый такой прогон выглядит для приёмника ровно как ещё один игрок: тот же
// публичный /metrics/report, тот же installId, те же события. В сводке админки
// они попадали в «уникальные установки» и «уникальные игроки» — то есть ровно в
// те два числа, ради которых на сводку и смотрят.
//
// Глушить отправку на клиенте (CHILLHUB_METRICS=0) для этого мало: так прогон
// перестаёт проверять и сам приём событий, а на уже накопленные события
// переменная окружения не действует вовсе. Поэтому у служебных прогонов есть
// признак В САМОМ событии, а сводка отбрасывает их при чтении: правило
// применяется и к тем строкам, что легли в файл до его появления.

// TestInstallIDPrefix помечает установку как служебную. Он выбран так, чтобы
// его нельзя было получить случайно: настоящий installId — это GUID без
// дефисов, то есть 32 шестнадцатеричных знака, и начаться с "test-" он не
// может.
const TestInstallIDPrefix = "test-"

// isSynthetic reports whether the event came from one of our own automated
// runs rather than from a player.
//
// The check is on installId alone. A test could also be recognised by its
// appVersion (CI builds are 0.0.0-ci), but that would quietly disown every
// event from a player running a self-built launcher, and it says nothing about
// events sent straight to the endpoint by a script that has no build at all.
func isSynthetic(installID string) bool {
	return strings.HasPrefix(strings.ToLower(strings.TrimSpace(installID)), TestInstallIDPrefix)
}
