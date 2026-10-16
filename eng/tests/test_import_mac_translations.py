import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SPEC = importlib.util.spec_from_file_location(
    "import_mac_translations", Path(__file__).resolve().parents[1] / "Import-MacTranslations.py")
IMPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMPORTER)


class TraditionalChineseImportTests(unittest.TestCase):
    def translate(self, localizations):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Localizable.xcstrings"
            path.write_text(json.dumps({"strings": {"Restart Now": {"localizations": localizations}}}), encoding="utf-8")
            return IMPORTER.mac_translations(path)["zh-TW"]

    def test_accepts_traditional_script_tag(self):
        result = self.translate({"zh-Hant": {"stringUnit": {"state": "translated", "value": "立即重新啟動"}}})
        self.assertEqual({"Restart Now": "立即重新啟動"}, result)

    def test_prefers_regional_translation_over_script_tag(self):
        result = self.translate({
            "zh-Hant": {"stringUnit": {"state": "translated", "value": "重新啟動"}},
            "zh-TW": {"stringUnit": {"state": "translated", "value": "立即重新啟動"}},
        })
        self.assertEqual({"Restart Now": "立即重新啟動"}, result)

    def test_does_not_import_untranslated_text(self):
        result = self.translate({"zh-Hant": {"stringUnit": {"state": "new", "value": "Restart Now"}}})
        self.assertEqual({}, result)


if __name__ == "__main__":
    unittest.main()
