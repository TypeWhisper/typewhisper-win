import importlib.util
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("granite_server", Path(__file__).parents[1] / "Scripts" / "granite_speech_server.py")
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class SetupTests(unittest.TestCase):
    def test_setup_downloads_chat_template_and_reports_byte_progress(self):
        files = {"model.safetensors": 1000, "chat_template.jinja": 10, "config.json": 5, "README.md": 200}
        calls, progress = [], []

        class FakeBar:
            def __init__(self, *args, **kwargs): pass
            def update(self, amount): pass

        class Api:
            def model_info(self, name, **kwargs):
                self_revision = kwargs["revision"]
                assert self_revision == server.MODEL_REVISION
                return types.SimpleNamespace(siblings=[types.SimpleNamespace(rfilename=f, size=size) for f, size in files.items()])

        def download(name, filename, revision, tqdm_class):
            calls.append((filename, revision))
            bar = tqdm_class(total=files[filename])
            bar.update(files[filename] // 2)
            bar.update(files[filename] - files[filename] // 2)

        modules = {
            "huggingface_hub": types.SimpleNamespace(HfApi=Api, hf_hub_download=download),
            "tqdm.auto": types.SimpleNamespace(tqdm=FakeBar),
        }
        with patch.dict(sys.modules, modules), patch.object(server, "respond", progress.append):
            server.cmd_setup()
        self.assertEqual([f for f, _ in calls], ["model.safetensors", "chat_template.jinja", "config.json"])
        self.assertTrue(all(revision == server.MODEL_REVISION for _, revision in calls))
        self.assertAlmostEqual(progress[1]["progress"], 500 / 1015)
        self.assertEqual(progress[-1], {"progress": 1.0, "phase": "done"})


if __name__ == "__main__":
    unittest.main()
