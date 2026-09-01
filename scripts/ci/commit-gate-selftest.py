#!/usr/bin/env python3
# Самопроверка гейта «в сообщениях нет упоминаний ИИ».
#
# ЗАЧЕМ. Гейт живёт в .github/workflows/ci.yml (job `commits`) и однажды уже
# молчал: при ручном запуске без PR `github.base_ref` пуст, диапазон получался
# вида «origin/..HEAD», git падал на разборе аргумента, а `|| true`, навешенный
# на весь конвейер, гасил и падение тоже. Шаг печатал «чисто» на ветке, в
# каждом коммите которой стоял трейлер. Молчащая проверка неотличима от
# пройденной — ровно этим она и опасна.
#
# Второй дырой был охват: проверялись только коммиты ветки, тогда как при
# сквош-мерже темой коммита в main становится ЗАГОЛОВОК PR, и он уезжает в
# публичные version.json и summary.json (CLAUDE.md).
#
# КАК. Скрипт читает тело шага ИЗ САМОГО ci.yml и исполняет его настоящим bash
# на заведомо грязных и заведомо чистых входах. Второй копии правил здесь нет
# намеренно: список, переписанный в проверку, разъехался бы с оригиналом, и
# самопроверка стала бы врать вместе с ним. Диапазон коммитов проверяется на
# одноразовом репозитории во временном каталоге — чтобы результат не зависел
# ни от истории этой ветки, ни от того, какие ссылки завёл checkout.
#
# ЛОЖНЫЙ ЗЕЛЁНЫЙ ЗАПРЕЩЁН. Пропавшая job, переименованный шаг, подстановка
# ${{ }} в теле (её здесь быть не должно: значения приходят через env) — это
# ОШИБКА, а не повод пропустить проверку.

import pathlib
import subprocess
import sys
import tempfile

import yaml

CI_YML = pathlib.Path(".github/workflows/ci.yml")
JOB = "commits"
STEP = "Проверить сообщения"
DIRTY_COMMIT = "Ускорить сборку\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n"
CLEAN_COMMIT = "Не выдавать недоставленное уведомление за успех\n"


class Unsupported(Exception):
    """Проверка не смогла проверить. Всегда фатальна."""


def gate_script():
    if not CI_YML.is_file():
        raise Unsupported(f"нет файла {CI_YML} (запуск не из корня репозитория?)")
    doc = yaml.safe_load(CI_YML.read_text(encoding="utf-8"))
    try:
        steps = doc["jobs"][JOB]["steps"]
    except (KeyError, TypeError) as exc:
        raise Unsupported(f"в {CI_YML} не нашлось jobs.{JOB}.steps: {exc}") from exc

    for step in steps:
        if isinstance(step, dict) and step.get("name") == STEP:
            body = step.get("run")
            if not body:
                raise Unsupported(f"у шага «{STEP}» нет тела run")
            if "${{" in body:
                raise Unsupported(
                    f"в теле шага «{STEP}» осталась подстановка ${{{{ }}}}: значения "
                    "обязаны приходить через env — иначе их нельзя ни проверить "
                    "здесь, ни защитить от подстановки чужого текста"
                )
            return body

    raise Unsupported(f"в job {JOB} нет шага «{STEP}» — его переименовали?")


def git(cwd, *args):
    res = subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True, text=True, encoding="utf-8"
    )
    if res.returncode != 0:
        raise Unsupported(f"git {' '.join(args)} не удался: {res.stderr.strip()}")


def make_repo(root, head_message):
    """Репозиторий с одной базой (origin/base) и одним коммитом поверх неё."""
    git(root, "init", "--quiet", "--initial-branch", "work")
    git(root, "config", "user.email", "selftest@example.invalid")
    git(root, "config", "user.name", "selftest")
    git(root, "config", "commit.gpgsign", "false")
    (root / "base.txt").write_text("base\n", encoding="utf-8")
    git(root, "add", "base.txt")
    git(root, "commit", "--quiet", "-m", "База")
    # Ветка базы — под тем же именем, что ждёт гейт: origin/<base_ref>.
    git(root, "update-ref", "refs/remotes/origin/base", "HEAD")
    (root / "head.txt").write_text("head\n", encoding="utf-8")
    git(root, "add", "head.txt")
    git(root, "commit", "--quiet", "-m", head_message)


def run_gate(script, cwd, base_ref, pr_title):
    # Переменные задаются через `env`, а не через окружение процесса: пустое
    # значение в окружении Windows означает «переменной нет», а проверять надо
    # именно «переменная есть и пуста» — так её видит гейт на workflow_dispatch.
    #
    # Тело уходит в stdin байтами, а не аргументом и не текстом: многострочный
    # аргумент переживает не всякая связка «python → bash», а текстовый режим на
    # Windows дописал бы в конец каждой строки CR, и bash споткнулся бы уже на
    # `set -euo pipefail`.
    proc = subprocess.Popen(
        ["env", f"BASE_REF={base_ref}", f"PR_TITLE={pr_title}", "bash", "-s"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
    )
    out, err = proc.communicate(script.encode("utf-8"))
    return proc.returncode, out.decode("utf-8", "replace") + err.decode("utf-8", "replace")


# (описание, сообщение коммита ветки, base_ref, заголовок PR, ожидаемый исход)
# «упал» — это любой ненулевой код: git и bash возвращают разное, и цепляться
# за конкретное число здесь незачем.
CASES = [
    ("трейлер в коммите ветки ловится", DIRTY_COMMIT, "base", "", "упал"),
    ("чистая ветка проходит", CLEAN_COMMIT, "base", "", "прошёл"),
    (
        "трейлер в заголовке PR ловится (сквош-мерж сделает его темой коммита)",
        CLEAN_COMMIT,
        "base",
        # ОДНА СТРОКА, и трейлер дописан в её конец: поле title на GitHub
        # многострочным не бывает. Раньше здесь стоял вход с переводами
        # строк — на нём срабатывал якорь `^`, случай считался покрытым,
        # а настоящий однострочный заголовок гейт пропускал.
        "Ускорить сборку Co-Authored-By: Claude <noreply@anthropic.com>",
        "упал",
    ),
    (
        "трейлер в скобках посреди заголовка ловится",
        CLEAN_COMMIT,
        "base",
        "Починить обрыв (Co-authored-by: Claude)",
        "упал",
    ),
    (
        "«generated with» в заголовке PR ловится без диапазона коммитов",
        CLEAN_COMMIT,
        "",
        "Убрать дубли (generated with Claude)",
        "упал",
    ),
    (
        "робот в заголовке PR ловится",
        CLEAN_COMMIT,
        "",
        "Ускорить сборку 🤖",
        "упал",
    ),
    (
        "чистый заголовок PR проходит",
        CLEAN_COMMIT,
        "",
        "Починить обрыв скачивания больших файлов",
        "прошёл",
    ),
    ("пустой base_ref не выдаётся за проверенный диапазон", CLEAN_COMMIT, "", "", "пропуск"),
    (
        "недоступный диапазон роняет шаг, а не печатает «чисто»",
        CLEAN_COMMIT,
        "такой-ветки-нет",
        "",
        "упал",
    ),
]


def main():
    script = gate_script()
    failures = []

    with tempfile.TemporaryDirectory() as tmp:
        for i, (name, message, base_ref, title, want) in enumerate(CASES):
            root = pathlib.Path(tmp) / f"repo{i}"
            root.mkdir()
            make_repo(root, message)

            code, out = run_gate(script, root, base_ref, title)
            ok = code == 0

            if want == "упал":
                good = not ok
            elif want == "прошёл":
                good = ok
            elif want == "пропуск":
                # Мало вернуть ноль: гейт обязан СКАЗАТЬ, что коммиты он не
                # смотрел. Раньше он в этом случае молча печатал «чисто».
                good = ok and "::notice::" in out
            else:  # pragma: no cover — опечатка в таблице выше
                raise Unsupported(f"неизвестный ожидаемый исход: {want}")

            print(f"[{'ok' if good else 'ПРОВАЛ'}] {name} (код {code})")
            if not good:
                failures.append((name, want, code, out.strip()))

    if failures:
        print()
        for name, want, code, out in failures:
            print(f"::error::самопроверка гейта: «{name}» — ожидалось «{want}», код {code}")
            for line in out.splitlines():
                print(f"  | {line}")
        return 1

    print("\nгейт ведёт себя как обещано")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Unsupported as exc:
        print(f"::error::{exc}")
        sys.exit(2)
