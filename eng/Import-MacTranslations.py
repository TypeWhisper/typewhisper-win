#!/usr/bin/env python3
"""Fill the Windows translation catalogs from the macOS string catalog.

Both apps use the English text as the key, so a text that reads the same on both platforms can reuse the macOS
translation. The script collects every Loc.T("...") text in src/, looks it up in Localizable.xcstrings and writes
the matches to src/TypeWhisper.Core/Localization/<language>.json. Translations already in a catalog are kept.

    python eng/Import-MacTranslations.py path/to/typewhisper-mac/TypeWhisper/Resources/Localizable.xcstrings

It ends by listing the texts that still need a translation, and exits with 1 if there are any.
"""
import argparse
import json
import re
import sys
from pathlib import Path

LANGUAGES = ["de", "ja", "zh-Hans"]
ROOT = Path(__file__).resolve().parent.parent
CATALOGS = ROOT / "src" / "TypeWhisper.Core" / "Localization"
CALL = re.compile(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"')
ESCAPE = re.compile(r'\\(u[0-9A-Fa-f]{4}|.)')
MAC_PLACEHOLDER = re.compile(r'%(?:(\d+)\$)?(?:\.(\d+))?(@|l{0,2}[du]|f)|%%')
SIMPLE_ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "0": "\0"}


def unescape(literal):
    """Returns the text of a regular C# string literal."""
    def replace(match):
        token = match.group(1)
        if token[0] == "u" and len(token) == 5:
            return chr(int(token[1:], 16))
        return SIMPLE_ESCAPES.get(token, token)
    return ESCAPE.sub(replace, literal)


def source_texts():
    texts = set()
    for path in (ROOT / "src").rglob("*.cs"):
        if {"obj", "bin"} & set(path.parts):
            continue
        texts.update(unescape(match) for match in CALL.findall(path.read_text(encoding="utf-8")))
    return texts


def to_windows(text):
    """Turns %@, %lld or %1$@ into {0}, {1}, ... so the text can be used with string.Format."""
    position = 0

    def replace(match):
        nonlocal position
        if match.group(0) == "%%":
            return "%"
        index = int(match.group(1)) - 1 if match.group(1) else position
        position += 1
        precision = ":F" + match.group(2) if match.group(2) else ""
        return "{" + str(index) + precision + "}"
    return MAC_PLACEHOLDER.sub(replace, text.replace("{", "{{").replace("}", "}}"))


def mac_translations(path):
    """Returns {language: {windows key: translation}} for every translated macOS string."""
    result = {language: {} for language in LANGUAGES}
    for key, entry in json.loads(Path(path).read_text(encoding="utf-8"))["strings"].items():
        localizations = entry.get("localizations", {})
        # A few English texts differ from their key; the visible English text is what Windows would use too.
        english = localizations.get("en", {}).get("stringUnit", {}).get("value", key)
        for language in LANGUAGES:
            unit = localizations.get(language, {}).get("stringUnit")
            if unit and unit.get("state") == "translated" and unit.get("value"):
                result[language].setdefault(to_windows(english), to_windows(unit["value"]))
                result[language].setdefault(to_windows(key), to_windows(unit["value"]))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("catalog", nargs="?", help="path to the macOS Localizable.xcstrings; omit to only list missing texts")
    arguments = parser.parse_args()
    texts = source_texts()
    mac = mac_translations(arguments.catalog) if arguments.catalog else {language: {} for language in LANGUAGES}
    missing = 0
    CATALOGS.mkdir(parents=True, exist_ok=True)
    for language in LANGUAGES:
        path = CATALOGS / (language + ".json")
        catalog = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
        imported = 0
        for text in sorted(texts - catalog.keys()):
            if text in mac[language]:
                catalog[text] = mac[language][text]
                imported += 1
        path.write_text(json.dumps(dict(sorted(catalog.items())), ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        todo = sorted(texts - catalog.keys())
        unused = sorted(catalog.keys() - texts)
        missing += len(todo)
        print(f"{language}: {imported} imported, {len(todo)} missing, {len(unused)} no longer used")
        for text in todo:
            print("  missing: " + json.dumps(text, ensure_ascii=False))
        for text in unused:
            print("  unused:  " + json.dumps(text, ensure_ascii=False))
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
