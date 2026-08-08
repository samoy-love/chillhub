#!/usr/bin/env python3
# Сверка двух фильтров путей в .github/workflows/deploy.yml.
#
# ЗАЧЕМ. В deploy.yml их два, и они решают разные задачи:
#
#   on.push.paths   пускать ли прогон вообще;
#   job `changes`   какие из четырёх целей в этом прогоне поедут.
#
# Первый обязан быть НАДМНОЖЕСТВОМ второго. Путь, по которому `changes` выкатил
# бы цель, но который не проходит `paths`, отменяет выкатку МОЛЧА: прогона нет,
# значит нет и красного, значит никто не узнает. Прод остаётся на старых файлах
# столько, сколько никто не смотрит. Это ровно тот класс отказа, из-за которого
# семь конфигов nginx годами не выезжали при зелёных деплоях.
#
# Сегодня надмножество соблюдено, и правило записано комментарием над `on`.
# Комментарий не проверяется ничем: правку в `changes` можно внести, не читая
# его. Отсюда эта проверка.
#
# КАК. Оба списка читаются ИЗ САМОГО deploy.yml — третьей копии данных здесь
# нет намеренно: список, скопированный в проверку, разойдётся с оригиналом
# точно так же, как расходятся эти два, и проверка станет врать.
#
# Из каждого регулярного выражения `changes` выводится минимальный путь-образец,
# который под него подпадает, и этот путь прогоняется через глобы `paths`. Не
# подошёл ни один — расхождение.
#
# ГРАНИЦА ТОЧНОСТИ. Образец берётся минимальный. Греп без якоря на конце
# (`^\.htmlhintrc`) подпадает и на `.htmlhintrc.bak`, но проверяется только сам
# `.htmlhintrc`: расширения хвоста дали бы ложные срабатывания на путях, которых
# в репозитории нет и не будет. Реальный сценарий дрейфа — «в `changes` завели
# новый каталог» — ловится полностью.
#
# ЛОЖНЫЙ ЗЕЛЁНЫЙ ЗАПРЕЩЁН. Всё, чего скрипт не понял — незнакомая конструкция в
# регулярном выражении, пустой список, пропавшая job'а, неразвёрнутая переменная
# — это ОШИБКА, а не повод пропустить проверку. Проверка, которая не смогла
# проверить, обязана гореть красным: молчащая проверка неотличима от пройденной,
# и именно так выглядит защищаемый ею дефект.

import re
import sys
import pathlib

import yaml

DEPLOY_YML = pathlib.Path(".github/workflows/deploy.yml")

# Подстановки для конструкций регулярного выражения, у которых нет единственного
# представителя. Ни один не содержит `/`: он значим и для `[^/]+`, и для глобов.
SAMPLE = "sample"
LEAF = "file"


class Unsupported(Exception):
    """Конструкция, о которой скрипт не может судить. Всегда фатальна."""


# --------------------------------------------------------------------------
# Разбор deploy.yml
# --------------------------------------------------------------------------


def load_workflow(path):
    if not path.is_file():
        die(f"нет файла {path} — проверять нечего")
    with path.open(encoding="utf-8") as fh:
        doc = yaml.safe_load(fh)
    if not isinstance(doc, dict):
        die(f"{path}: ожидался маппинг на верхнем уровне")
    return doc


def triggers(doc):
    # YAML 1.1 читает голое `on:` как булево True, и PyYAML делает именно так.
    # Оба написания обязаны разбираться: иначе проверка развалится от кавычек.
    for key in ("on", True, "On", "ON"):
        if key in doc:
            return doc[key]
    die("в deploy.yml нет блока триггеров `on`")


def push_paths(doc):
    on = triggers(doc)
    if not isinstance(on, dict) or "push" not in on:
        die("в deploy.yml нет триггера `push` — сверять фильтр не с чем")
    push = on["push"] or {}
    if not isinstance(push, dict):
        die("`on.push` не маппинг")
    if "paths" not in push:
        # Фильтра нет — проходит всё, надмножество выполняется тривиально.
        # Это законное состояние, а не пропуск проверки.
        return None
    paths = push["paths"]
    if not isinstance(paths, list) or not paths:
        die("`on.push.paths` пуст или не список")
    return [str(p) for p in paths]


def changes_script(doc):
    jobs = doc.get("jobs")
    if not isinstance(jobs, dict) or "changes" not in jobs:
        die("в deploy.yml нет job'ы `changes` — расчёт целей переехал, проверку надо чинить")
    steps = jobs["changes"].get("steps")
    if not isinstance(steps, list):
        die("у job'ы `changes` нет шагов")
    script = "\n".join(s["run"] for s in steps if isinstance(s, dict) and isinstance(s.get("run"), str))
    if not script.strip():
        die("у job'ы `changes` нет ни одного шага с `run`")
    return script


# --------------------------------------------------------------------------
# Извлечение образцов регулярных выражений из shell-скрипта `changes`
# --------------------------------------------------------------------------

GREP_RE = re.compile(r"""grep\s+(-[A-Za-z]+)\s+(['"])(.*?)\2""")
ASSIGN_RE = re.compile(r"""^\s*([A-Za-z_][A-Za-z0-9_]*)=(['"])(.*?)\2\s*$""", re.M)
VAR_RE = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?")


def grep_patterns(script):
    """Положительные ERE-шаблоны, которыми `changes` разбирает список файлов."""
    env = {m.group(1): m.group(3) for m in ASSIGN_RE.finditer(script)}
    patterns = []
    for flags, _quote, raw in (m.groups() for m in GREP_RE.finditer(script)):
        # `-v` инвертирует: такой греп сужает выборку, а не расширяет её, и
        # цель по нему не поедет. Надмножество он не обязывает ни к чему.
        if "v" in flags:
            continue
        if "E" not in flags:
            raise Unsupported(f"греп без -E, синтаксис шаблона неизвестен: grep {flags} '{raw}'")
        expanded = VAR_RE.sub(lambda m: env.get(m.group(1), m.group(0)), raw)
        left = VAR_RE.search(expanded)
        if left and left.group(1) not in env:
            raise Unsupported(f"неразвёрнутая переменная ${left.group(1)} в шаблоне '{raw}'")
        patterns.append((raw, expanded))
    if not patterns:
        raise Unsupported("в `changes` не нашлось ни одного грепа — разбор сломался")
    return patterns


# --------------------------------------------------------------------------
# Минимальный разворот ERE в конкретные пути
# --------------------------------------------------------------------------


def expand(pattern):
    """ERE -> [(путь, привязан ли конец)]. Всё непонятое — Unsupported."""
    if not pattern.startswith("^"):
        raise Unsupported(f"шаблон не привязан к началу строки: '{pattern}'")
    branches, pos = _alt(pattern, 1)
    if pos != len(pattern):
        raise Unsupported(f"шаблон разобран не до конца: '{pattern}' (остаток '{pattern[pos:]}')")
    return branches


def _alt(s, i):
    branches, i = _seq(s, i)
    while i < len(s) and s[i] == "|":
        more, i = _seq(s, i + 1)
        branches += more
    return branches, i


def _seq(s, i):
    out = [("", False)]

    def append(text):
        for _, ended in out:
            if ended:
                raise Unsupported(f"текст после '$' в '{s}'")
        return [(t + text, e) for t, e in out]

    while i < len(s) and s[i] not in "|)":
        c = s[i]
        if c == "\\":
            if i + 1 >= len(s):
                raise Unsupported(f"обрыв на '\\' в '{s}'")
            nxt = s[i + 1]
            if nxt.isalnum():
                raise Unsupported(f"класс-escape \\{nxt} в '{s}'")
            out = append(nxt)
            i += 2
        elif c == "(":
            inner, j = _alt(s, i + 1)
            if j >= len(s) or s[j] != ")":
                raise Unsupported(f"незакрытая группа в '{s}'")
            i = j + 1
            if i < len(s) and s[i] in "*+?{":
                raise Unsupported(f"квантификатор на группе в '{s}'")
            merged = []
            for text, ended in out:
                if ended:
                    raise Unsupported(f"текст после '$' в '{s}'")
                for itext, iended in inner:
                    merged.append((text + itext, iended))
            out = merged
        elif c == "[":
            j = s.find("]", i)
            if j < 0:
                raise Unsupported(f"незакрытый класс в '{s}'")
            klass = s[i : j + 1]
            quant = s[j + 1] if j + 1 < len(s) else ""
            # Единственный класс, встречающийся в `changes`: «файл в корне
            # каталога». Любой другой — повод остановиться, а не угадать.
            if klass != "[^/]" or quant != "+":
                raise Unsupported(f"класс {klass}{quant} в '{s}'")
            out = append(SAMPLE)
            i = j + 2
        elif c == "$":
            if i + 1 != len(s) and s[i + 1] not in "|)":
                raise Unsupported(f"'$' не в конце ветки в '{s}'")
            out = [(t, True) for t, _ in out]
            i += 1
        elif c in "*+?{}^.":
            raise Unsupported(f"метасимвол '{c}' в '{s}'")
        else:
            out = append(c)
            i += 1
    return out, i


def witnesses(pattern):
    """Конкретные пути, при которых `changes` считает цель задетой."""
    paths = []
    for text, ended in expand(pattern):
        if not text:
            raise Unsupported(f"шаблон подпадает на пустой путь: '{pattern}'")
        if ended:
            paths.append(text)
        elif text.endswith("/"):
            # Каталог: и файл прямо в нём, и файл в подкаталоге. Второй нужен,
            # чтобы одноуровневый глоб (`landing/*`) не сошёл за `landing/**`.
            paths.append(text + LEAF)
            paths.append(text + LEAF + "/" + LEAF)
        else:
            paths.append(text)
    return paths


# --------------------------------------------------------------------------
# Глобы GitHub из on.push.paths
# --------------------------------------------------------------------------


def glob_to_regex(glob):
    if glob.startswith("!"):
        raise Unsupported(f"отрицающий глоб '{glob}' — надмножество так не доказать")
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("^" + "".join(out) + "$")


# --------------------------------------------------------------------------


def check(paths, script):
    if paths is None:
        print("on.push.paths не задан — прогон заводит любой коммит, надмножество тривиально")
        return 0

    globs = [(g, glob_to_regex(g)) for g in paths]
    print("фильтр на триггере (on.push.paths):")
    for g, _ in globs:
        print(f"  {g}")

    print("\nшаблоны расчёта целей (job changes):")
    problems = []
    for raw, pattern in grep_patterns(script):
        shown = raw if raw == pattern else f"{raw}  ->  {pattern}"
        print(f"  {shown}")
        for path in witnesses(pattern):
            hit = next((g for g, rx in globs if rx.match(path)), None)
            if hit is None:
                problems.append((pattern, path))
                print(f"      {path}  НЕ ПРОХОДИТ ни один глоб")
            else:
                print(f"      {path}  <- {hit}")

    if problems:
        print()
        for pattern, path in problems:
            print(
                f"::error file=.github/workflows/deploy.yml::шаблон '{pattern}' в job'е changes выкатил бы цель "
                f"по пути {path}, но on.push.paths такой коммит не пропускает — прогона не будет, "
                f"красного не будет, цель просто перестанет ездить. Добавьте путь в on.push.paths"
            )
        return 1

    print("\non.push.paths — надмножество того, что смотрит changes")
    return 0



# --------------------------------------------------------------------------
# Третий фильтр: гейт роста версии лаунчера (ci.yml, job launcher-version-bump)
# --------------------------------------------------------------------------
#
# ЗАЧЕМ. Списков стало три, а не два. Третий решает, спрашивать ли рост
# `<Version>`, и обязан покрывать всё, от чего пересобирается установщик:
# сами файлы клиента И то, что валит `changes` в фолбэк «катим всё»
# (описания целей, сам deploy.yml). Разъехавшись, он пропускает PR, после
# мержа которого установщик собирается со старой, уже опубликованной версией
# и падает на публикации — уже после мержа, когда чинить дороже.
#
# Так и случилось 2026-08-08: PR трогал только `.deploy-kit/*.env`, гейт это
# не заметил, и выкатка упала на «launcher version 1.3.2 is already
# published». Правило записали комментарием, а комментарий не проверяется.

CI_YML = pathlib.Path(".github/workflows/ci.yml")

# Шаблоны `changes`, которые ведут к пересборке установщика. Опознаются по
# кускам, которые в них обязаны быть: у фолбэка — описания целей и сам
# пайплайн, у сборки установщика — её скрипт.
INSTALLER_MARKERS = ("deploy-kit", "build-installer")


def version_gate_pattern():
    """ERE, которым `launcher-version-bump` решает, спрашивать ли рост версии."""
    doc = load_workflow(CI_YML)
    jobs = doc.get("jobs")
    if not isinstance(jobs, dict) or "launcher-version-bump" not in jobs:
        die("в ci.yml нет job'ы `launcher-version-bump` — гейт версии переехал, "
            "проверку надо чинить")
    steps = jobs["launcher-version-bump"].get("steps")
    if not isinstance(steps, list):
        die("у job'ы `launcher-version-bump` нет шагов")
    script = "\n".join(
        s["run"] for s in steps if isinstance(s, dict) and isinstance(s.get("run"), str)
    )
    pats = [p for _raw, p in grep_patterns(script)]
    if len(pats) != 1:
        die(f"в `launcher-version-bump` ожидался ровно один положительный греп, "
            f"найдено {len(pats)}: разбирать несколько шаблонов эта сверка не умеет")
    return pats[0]


def check_version_gate(script):
    """Гейт версии обязан покрывать всё, от чего пересобирается установщик."""
    gate_raw = version_gate_pattern()
    # Ветки шаблона, а не re: expand уже разобрал ERE в пары «текст, привязан
    # ли конец», и второй разбор тем же файлом двумя способами разошёлся бы.
    gate_branches = expand(gate_raw)

    def covered(path):
        return any(path == text if ended else path.startswith(text)
                   for text, ended in gate_branches)

    print("\nгейт роста версии (ci.yml, launcher-version-bump):")
    print(f"  {gate_raw}")

    relevant = [
        (raw, pat)
        for raw, pat in grep_patterns(script)
        if any(m in raw for m in INSTALLER_MARKERS)
    ]
    if not relevant:
        die("в `changes` не нашлось ни одного шаблона про установщик — "
            f"опознание идёт по кускам {INSTALLER_MARKERS}, и оно устарело")

    problems = []
    for raw, pattern in relevant:
        for path in witnesses(pattern):
            if not covered(path):
                problems.append((raw, path))
                print(f"      {path}  гейт версии ПРОПУСКАЕТ (из {raw})")
    if problems:
        print(
            "\nгейт версии УЖЕ гейт: путь, по которому установщик пересобирается, "
            "но роста версии не спрашивают, означает падение публикации ПОСЛЕ мержа"
        )
        return 1
    print("  покрывает все пути пересборки установщика")
    return 0

def die(msg):
    print(f"::error file={DEPLOY_YML}::{msg}")
    sys.exit(1)


# --------------------------------------------------------------------------
# Самопроверка: доказывает, что проверка умеет краснеть.
#
# Без неё скрипт, разучившийся находить грепы, молча выдавал бы «всё хорошо» —
# то есть повторил бы собой ровно тот дефект, который сторожит.
# --------------------------------------------------------------------------

SELF_TESTS = [
    # (описание, глобы, шаблоны в changes, ждём ли провал)
    ("совпадающая пара", ["landing/**"], ["^landing/"], False),
    ("одноуровневый глоб не покрывает подкаталоги", ["landing/*"], ["^landing/"], True),
    ("путь заведён только в changes", ["landing/**"], ["^landing/", "^deploy/"], True),
    ("файл в корне модуля", ["server/**"], ["^server/(cmd/api/|[^/]+$)"], False),
    ("глоб уже, чем ветка группы", ["server/cmd/**"], ["^server/(cmd/api/|internal/)"], True),
    ("префикс без якоря", [".stylelint*"], ["^\\.stylelintrc\\.json"], False),
]


def self_test():
    ok = True
    for name, globs, patterns, want_fail in SELF_TESTS:
        script = "\n".join(f"echo \"$FILES\" | grep -qE '{p}'" for p in patterns)
        failed = check(globs, script) != 0
        verdict = "ok" if failed == want_fail else "СЛОМАНО"
        if failed != want_fail:
            ok = False
        print(f"-- самопроверка: {name}: {verdict}\n")
    if not ok:
        print("::error::самопроверка сверки путей не прошла — проверке нельзя верить")
        return 1
    print("самопроверка пройдена")
    return 0


def main(argv):
    try:
        if "--self-test" in argv:
            return self_test()
        doc = load_workflow(DEPLOY_YML)
        script = changes_script(doc)
        rc = check(push_paths(doc), script)
        # Второй сверкой, а не вместо первой: списки разные и ломаются
        # независимо. Возвращается худший из двух исходов.
        return max(rc, check_version_gate(script))
    except Unsupported as exc:
        die(f"сверка не смогла разобрать deploy.yml: {exc}. Пока непонятное не разобрано, "
            f"считать фильтр надмножеством нельзя")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
