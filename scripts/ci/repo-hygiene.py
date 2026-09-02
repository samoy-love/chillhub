#!/usr/bin/env python3
# Инварианты сборки, выкатки и гейтов, которые больше некому проверить.
#
#   python scripts/ci/repo-hygiene.py             # проверить дерево
#   python scripts/ci/repo-hygiene.py --self-test # проверить сами проверки
#
# ЗАЧЕМ. Всё, что ниже, — это правила, записанные до сих пор только прозой: в
# комментарии рядом с кодом, в шапке workflow, в CLAUDE.md. Каждое из них уже
# один раз разошлось с кодом, и разошлось молча:
#
#   * scripts/deploy.sh ставил файлы, удалённые из репозитория, и под `set -e`
#     рвался на них ПОСЛЕ подмены боевых бинарей;
#   * ротация снимков в deploy/backup-content.sh удаляла каталоги .FAILED,
#     хотя комментарий строкой выше обещал обратное;
#   * постусловие «установщик собрался» в build-installer.ps1 было Test-Path
#     по каталогу, который никто не чистит, — то есть проходило на файле
#     прошлой сборки;
#   * ESLint при переезде на flat config потерял базовый набор правил, а без
#     --max-warnings 0 не падал и на предупреждениях;
#   * шапка ci.yml требует пиннинга версий, а `npx --yes c8` тянул любую;
#   * codecov.yml ждал два отчёта из трёх и считал статус без клиентского.
#
# Общий признак у всех шести — отказ не виден: гейт остаётся зелёным, а
# расходится то, о чём он не спрашивает.
#
# ЛОЖНЫЙ ЗЕЛЁНЫЙ ЗАПРЕЩЁН. Пропавший файл, неразобранная строка, ноль
# проверенных мест — это ОШИБКА, а не повод пропустить проверку: проверка,
# которая не смогла проверить, неотличима от пройденной.

import pathlib
import re
import sys

ROOT = pathlib.Path(".")

# Каталоги, чьи шелл-скрипты обязаны ссылаться только на существующие файлы.
SHELL_DIRS = ("scripts", "deploy", "ci")

# Путь внутрь репозитория в тексте скрипта. Токены с $ не берём: там подстановка.
REPO_PATH_RE = re.compile(r"(?<![\w/.$-])((?:scripts|deploy|ci)/[\w./-]*[\w/])")

USES_RE = re.compile(r"^\s*-?\s*uses:\s*([^\s#]+)", re.MULTILINE)
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
NPX_RE = re.compile(r"npx\s+(?:--yes|-y)\s+(\S+)")

# Переиспользуемые workflow собственного deploy-kit. Это не «стороннее действие
# с мутабельным тегом», а единственный путь выкатки этого и соседних проектов
# (CLAUDE.md): прибей их к SHA — и починка выкатки перестанет доезжать до
# репозиториев, пока кто-нибудь не обновит хеш в каждом.
OWN_REUSABLE_PREFIX = "samoy-love/deploy-kit/"


class Unsupported(Exception):
    """Проверка не смогла проверить. Всегда фатальна."""


class Tree:
    """Дерево репозитория с возможностью подменить содержимое файла."""

    def __init__(self, overrides=None):
        self.overrides = dict(overrides or {})

    def read(self, rel):
        if rel in self.overrides:
            return self.overrides[rel]
        path = ROOT / rel
        if not path.is_file():
            raise Unsupported(f"нет файла {rel} — проверять нечего")
        return path.read_text(encoding="utf-8")

    def exists(self, rel):
        if rel in self.overrides:
            return self.overrides[rel] is not None
        return (ROOT / rel).exists()

    def shell_scripts(self):
        found = []
        for d in SHELL_DIRS:
            found.extend(sorted(str(p).replace("\\", "/") for p in (ROOT / d).rglob("*.sh")))
        for rel, text in self.overrides.items():
            if text is not None and rel.endswith(".sh") and rel not in found:
                found.append(rel)
        return [r for r in found if self.overrides.get(r, "") is not None]

    def workflows(self):
        return sorted(str(p).replace("\\", "/") for p in (ROOT / ".github/workflows").glob("*.yml"))


# --------------------------------------------------------------------------
# Проверки
# --------------------------------------------------------------------------


def check_shell_paths(tree):
    """Шелл-скрипты не ссылаются на файлы, которых в репозитории нет."""
    problems = []
    scripts = tree.shell_scripts()
    if not scripts:
        raise Unsupported("не нашлось ни одного шелл-скрипта — запуск не из корня?")
    for rel in scripts:
        for line_no, line in enumerate(tree.read(rel).splitlines(), 1):
            for token in REPO_PATH_RE.findall(line):
                token = token.rstrip("/")
                if not tree.exists(token):
                    problems.append(
                        f"{rel}:{line_no} ссылается на {token}, которого в репозитории нет"
                    )
    return problems


def check_backup_rotation(tree):
    """Ротация снимков не удаляет диагностические каталоги .FAILED."""
    rel = "deploy/backup-content.sh"
    text = tree.read(rel)
    rotations = [
        (n, l)
        for n, l in enumerate(text.splitlines(), 1)
        if "-name '20*'" in l and "find" in l
    ]
    if not rotations:
        raise Unsupported(f"в {rel} не нашлось глоба ротации по '20*' — его переписали?")
    problems = []
    for line_no, line in rotations:
        if "! -name '*.FAILED'" not in line:
            problems.append(
                f"{rel}:{line_no} ротация матчит и каталоги .FAILED — "
                "неудачный снимок удалится раньше, чем его разберут"
            )
    return problems


def check_installer_postcondition(tree):
    """Установщик удаляется до вызова makensis, а не только проверяется после."""
    rel = "scripts/build-installer.ps1"
    text = tree.read(rel)
    build = text.find('& "$makensis" @nsisArgs')
    if build < 0:
        raise Unsupported(f"в {rel} не нашлось вызова makensis — его переписали?")
    removal = text.find("Remove-Item -LiteralPath $setupExe")
    if removal < 0 or removal > build:
        return [
            f"{rel}: старый ChillHub-Setup.exe не удаляется перед makensis — "
            "постусловие «собрался» пройдёт на файле прошлой сборки"
        ]
    return []


def check_eslint_gate(tree):
    """Оба вызова ESLint роняют прогон на предупреждениях."""
    problems = []
    checked = 0
    for rel in (".github/workflows/ci.yml", "Makefile"):
        for line_no, line in enumerate(tree.read(rel).splitlines(), 1):
            if "eslint" not in line or '"landing/**/*.js"' not in line:
                continue
            checked += 1
            if "--max-warnings 0" not in line:
                problems.append(
                    f"{rel}:{line_no} ESLint без --max-warnings 0 — "
                    "предупреждения не роняют прогон"
                )
    if checked < 2:
        raise Unsupported(
            f"нашлось {checked} вызовов ESLint вместо двух (ci.yml и Makefile) — "
            "проверка не понимает, что проверять"
        )
    return problems


def check_pins(tree):
    """Действия прибиты к SHA, а разовые пакеты npx — к версии."""
    problems = []
    seen_uses = 0
    for rel in tree.workflows():
        text = tree.read(rel)
        for match in USES_RE.finditer(text):
            ref = match.group(1)
            if (
                ref.startswith("./")
                or ref.startswith("docker://")
                or ref.startswith(OWN_REUSABLE_PREFIX)
            ):
                continue
            seen_uses += 1
            line_no = text[: match.start()].count("\n") + 1
            if "@" not in ref or not SHA_RE.match(ref.rsplit("@", 1)[1]):
                problems.append(
                    f"{rel}:{line_no} {ref} прибито к тегу, а не к SHA — "
                    "тег мутабелен, и владелец действия может переставить его куда угодно"
                )
    if seen_uses == 0:
        raise Unsupported("в .github/workflows не нашлось ни одного uses: — запуск не из корня?")

    for rel in [*tree.workflows(), "Makefile"]:
        text = tree.read(rel)
        lines = text.splitlines()
        for match in NPX_RE.finditer(text):
            pkg = match.group(1)
            line_no = text[: match.start()].count("\n") + 1
            # Строка-комментарий — не команда: и Makefile, и workflow
            # рассказывают о прежних ошибках, приводя их дословно.
            if lines[line_no - 1].lstrip().startswith("#"):
                continue
            versioned = pkg.count("@") >= (2 if pkg.startswith("@") else 1)
            if not versioned:
                problems.append(
                    f"{rel}:{line_no} npx тянет {pkg} без версии — "
                    "прогон перестаёт быть воспроизводимым (см. шапку ci.yml)"
                )
    return problems


def check_codecov_builds(tree):
    """Статус Codecov считается после всех отчётов, а не после части."""
    rel = "codecov.yml"
    text = tree.read(rel)
    match = re.search(r"^\s*after_n_builds:\s*(\d+)\s*$", text, re.MULTILINE)
    if not match:
        raise Unsupported(f"в {rel} не нашлось after_n_builds — настройку убрали?")
    after = int(match.group(1))

    flags_at = re.search(r"^flags:\s*$", text, re.MULTILINE)
    if not flags_at:
        raise Unsupported(f"в {rel} не нашлось блока flags:")
    flags = []
    for line in text[flags_at.end():].splitlines()[1:]:
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith("  "):
            break
        name = re.match(r"^  ([\w-]+):\s*$", line)
        if name:
            flags.append(name.group(1))
    if not flags:
        raise Unsupported(f"в {rel} блок flags: пуст — проверка не понимает файл")

    if after != len(flags):
        return [
            f"{rel}: after_n_builds={after} при {len(flags)} флагах "
            f"({', '.join(flags)}) — статус посчитается до последнего отчёта"
        ]
    return []


CHECKS = (
    check_shell_paths,
    check_backup_rotation,
    check_installer_postcondition,
    check_eslint_gate,
    check_pins,
    check_codecov_builds,
)


# --------------------------------------------------------------------------
# Самопроверка: каждая проверка обязана покраснеть на заведомо сломанном входе
# --------------------------------------------------------------------------

BROKEN = {
    check_shell_paths: ("deploy/backup-content.sh", 'sudo install "scripts/deploy-nginx.sh" /x\n'),
    check_backup_rotation: (
        "deploy/backup-content.sh",
        "mapfile -t snaps < <(find \"$BACKUP_ROOT\" -type d -name '20*' -printf '%f\\n')\n",
    ),
    check_installer_postcondition: (
        "scripts/build-installer.ps1",
        '& "$makensis" @nsisArgs\nif (-not (Test-Path -LiteralPath $setupExe)) { throw "нет" }\n',
    ),
    check_eslint_gate: (
        "Makefile",
        'npx -y eslint@10.8.0 "landing/**/*.js" "server/admin_ui/**/*.js"\n',
    ),
    check_pins: ("Makefile", "npx -y htmlhint \"landing/**/*.html\"\n"),
    check_codecov_builds: (
        "codecov.yml",
        "codecov:\n  notify:\n    after_n_builds: 2\n\nflags:\n  server:\n    paths:\n      - server/\n  launcher:\n    paths:\n      - launcher/\n  web:\n    paths:\n      - landing/\n",
    ),
}


def self_test():
    failures = []
    for check in CHECKS:
        rel, text = BROKEN[check]
        broken = Tree({rel: text})
        try:
            problems = check(broken)
        except Unsupported as exc:
            problems = [f"(проверка не смогла проверить: {exc})"]
        if problems:
            print(f"[ok] {check.__name__} краснеет на сломанном входе")
        else:
            print(f"[ПРОВАЛ] {check.__name__} промолчала на сломанном входе")
            failures.append(check.__name__)

    if failures:
        print()
        for name in failures:
            print(f"::error::самопроверка: {name} не ловит собственный дефект")
        return 1
    print("\nвсе проверки краснеют, когда должны")
    return 0


def main(argv):
    if "--self-test" in argv:
        return self_test()

    problems = []
    for check in CHECKS:
        found = check(Tree())
        print(f"[{'ok' if not found else 'ПРОВАЛ'}] {check.__doc__.strip()}")
        problems.extend(found)

    if problems:
        print()
        for p in problems:
            print(f"::error::{p}")
        return 1
    print("\nинварианты соблюдены")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Unsupported as exc:
        print(f"::error::{exc}")
        sys.exit(2)
