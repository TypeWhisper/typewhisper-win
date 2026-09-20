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
    def test_translation_preserves_the_selected_source_language(self):
        self.assertEqual(server.transcription_question(True, "de"), "Translate the speech into English. The spoken language is German.")
        self.assertIn("Japanese", server.transcription_question(False, "ja"))
        self.assertEqual(server.transcription_question(True, None), "Translate the speech into English.")

    def test_cuda_precision_falls_back_on_older_gpus(self):
        torch = types.SimpleNamespace(bfloat16="bf16", float32="fp32", cuda=types.SimpleNamespace(is_bf16_supported=lambda: False))
        self.assertEqual(server.inference_dtype(torch, "cuda"), "fp32")
        torch.cuda.is_bf16_supported = lambda: True
        self.assertEqual(server.inference_dtype(torch, "cuda"), "bf16")
        self.assertEqual(server.inference_dtype(torch, "cpu"), "fp32")

    def test_long_recording_budget_and_limit_detection(self):
        self.assertGreater(server.generation_budget(180, 10000), 500)
        self.assertEqual(server.generation_budget(180, 700), 700)
        with self.assertRaises(ValueError):
            server.generation_budget(180, 0)
        with self.assertRaises(ValueError):
            server.generation_budget(float('nan'), 10000)
        with self.assertRaises(RuntimeError):
            server.check_generation_complete([1, 2, 3], 3, [4, 5])
        server.check_generation_complete([1, 2, 4], 3, [4, 5])
        server.check_generation_complete([1, 2, 4], 3, 4)
        server.check_generation_complete([1, 2], 3, None)

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
