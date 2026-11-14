import io
import importlib.util
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("granite_server", Path(__file__).parents[1] / "Scripts" / "granite_speech_server.py")
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)

MODEL_ENVIRONMENT = ("TYPEWHISPER_MODEL_DIR", "TYPEWHISPER_LEGACY_HF_HOME")


def model_environment(**values):
    """Sets the model folder variables for one test and removes those it does not name."""
    environment = patch.dict(server.os.environ, values)
    environment.start()
    for name in MODEL_ENVIRONMENT:
        if name not in values:
            server.os.environ.pop(name, None)
    return environment


class SetupTests(unittest.TestCase):
    def use_model_environment(self, **values):
        self.addCleanup(model_environment(**values).stop)

    def test_readiness_checks_the_pinned_cache_revision_and_missing_sentinel(self):
        legacy_home = os.path.join("profile", "hf-cache")
        self.use_model_environment(TYPEWHISPER_MODEL_DIR=os.path.join("profile", "missing-model"), TYPEWHISPER_LEGACY_HF_HOME=legacy_home)
        for cached in ('/cache/config.json', None, object()):
            responses = []
            def lookup(name, filename, *, revision, cache_dir):
                self.assertEqual((name, filename, revision), (server.MODEL_NAME, 'config.json', server.MODEL_REVISION))
                self.assertEqual(cache_dir, os.path.join(legacy_home, "hub"))
                return cached
            modules = {name: types.SimpleNamespace() for name in ('torch', 'transformers', 'soundfile')}
            modules['huggingface_hub'] = types.SimpleNamespace(try_to_load_from_cache=lookup)
            with patch.dict(sys.modules, modules), patch.object(server, 'respond', responses.append):
                server.cmd_check()
            self.assertEqual(responses[-1]['ready'], isinstance(cached, str))

    def test_readiness_uses_the_model_folder_without_the_cache(self):
        with tempfile.TemporaryDirectory() as root:
            open(os.path.join(root, "config.json"), "w").close()
            self.use_model_environment(TYPEWHISPER_MODEL_DIR=root)
            def lookup(*args, **kwargs):
                raise AssertionError("A complete model folder must not fall back to the cache.")
            modules = {name: types.SimpleNamespace() for name in ('torch', 'transformers', 'soundfile')}
            modules['huggingface_hub'] = types.SimpleNamespace(try_to_load_from_cache=lookup)
            responses = []
            with patch.dict(sys.modules, modules), patch.object(server, 'respond', responses.append):
                server.cmd_check()
            self.assertEqual(responses[-1], {"ready": True, "issues": []})

    def test_load_prefers_the_model_folder_and_falls_back_to_the_old_cache(self):
        with tempfile.TemporaryDirectory() as root:
            model = os.path.join(root, "model")
            legacy_home = os.path.join(root, "hf-cache")
            self.use_model_environment(TYPEWHISPER_MODEL_DIR=model, TYPEWHISPER_LEGACY_HF_HOME=legacy_home)
            self.assertEqual(server.model_source(), (server.MODEL_NAME, {"revision": server.MODEL_REVISION, "cache_dir": os.path.join(legacy_home, "hub")}))
            os.makedirs(model)
            open(os.path.join(model, "config.json"), "w").close()
            self.assertEqual(server.model_source(), (model, {}))

    def test_serve_loads_from_the_model_folder(self):
        with tempfile.TemporaryDirectory() as root:
            open(os.path.join(root, "config.json"), "w").close()
            self.use_model_environment(TYPEWHISPER_MODEL_DIR=root)
            calls = []

            class Loaded:
                tokenizer = object()
                def to(self, device): return self
                def eval(self): return self

            def loader(name):
                def from_pretrained(source, **kwargs):
                    calls.append((name, source, kwargs))
                    return Loaded()
                return types.SimpleNamespace(from_pretrained=from_pretrained)

            torch = types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False), set_num_threads=lambda _: None, float32="fp32")
            modules = {"torch": torch, "soundfile": types.SimpleNamespace(),
                       "transformers": types.SimpleNamespace(AutoModelForSpeechSeq2Seq=loader("model"), AutoProcessor=loader("processor"))}
            responses = []
            with patch.dict(sys.modules, modules), patch.dict(server.os.environ, {"TYPEWHISPER_DEVICE": "Cpu"}), \
                    patch.object(sys, "stdin", io.StringIO('{"cmd":"load","req_id":7}\n')), patch.object(server, "respond", responses.append):
                server.cmd_serve()
            self.assertEqual(responses[-1], {"status": "ok", "device": "cpu", "req_id": 7})
            self.assertEqual([(name, source) for name, source, _ in calls], [("processor", root), ("model", root)])
            self.assertTrue(all(kwargs["local_files_only"] and "revision" not in kwargs for _, _, kwargs in calls))

    def test_paths_beyond_max_path_are_reported_unless_long_paths_are_enabled(self):
        names = ["config.json", "model-00001-of-00003.safetensors"]
        root = os.path.abspath(os.sep)
        # The longest model file path in this folder is exactly MAX_PATH characters long.
        deep = os.path.join(root, "p" * (server.MAX_PATH - len(root) - len(names[1]) - 1))
        with patch.object(server, "long_paths_enabled", lambda: False):
            self.assertIsNone(server.path_length_error(os.path.abspath("m"), names))
            self.assertIsNone(server.path_length_error(deep[:-1], names))
            error = server.path_length_error(deep, names)
            self.assertIn(f"{server.MAX_PATH} characters", error)
            self.assertIn(deep, error)
        with patch.object(server, "long_paths_enabled", lambda: True):
            self.assertIsNone(server.path_length_error(deep, names))

    def test_setup_stops_before_downloading_into_a_too_long_folder(self):
        self.use_model_environment(TYPEWHISPER_MODEL_DIR="model")

        class Api:
            def model_info(self, name, **kwargs):
                return types.SimpleNamespace(siblings=[types.SimpleNamespace(rfilename="config.json", size=5)])

        def download(*args, **kwargs):
            raise AssertionError("Setup must not download into a folder Windows cannot open.")

        modules = {"huggingface_hub": types.SimpleNamespace(HfApi=Api, hf_hub_download=download)}
        responses = []
        with patch.dict(sys.modules, modules), patch.object(server, "path_length_error", lambda directory, names: "too long"), \
                patch.object(server, "respond", responses.append):
            with self.assertRaises(SystemExit):
                server.cmd_setup()
        self.assertEqual(responses[-1], {"error": "too long"})

    def test_setup_requires_a_model_folder(self):
        self.use_model_environment()
        responses = []
        with patch.object(server, "respond", responses.append):
            with self.assertRaises(SystemExit):
                server.cmd_setup()
        self.assertIn("error", responses[-1])

    def test_old_cache_files_move_into_the_model_folder(self):
        with tempfile.TemporaryDirectory() as root:
            model = os.path.join(root, "model")
            legacy_hub = os.path.join(root, "hf-cache", "hub")
            snapshot = os.path.join(legacy_hub, "snapshots", server.MODEL_REVISION)
            os.makedirs(snapshot)
            with open(os.path.join(snapshot, "model.safetensors"), "w") as cached_file:
                cached_file.write("weights")

            def lookup(name, filename, *, cache_dir, revision):
                self.assertEqual((name, cache_dir, revision), (server.MODEL_NAME, legacy_hub, server.MODEL_REVISION))
                path = os.path.join(snapshot, filename)
                return path if os.path.isfile(path) else None

            with patch.dict(sys.modules, {"huggingface_hub": types.SimpleNamespace(try_to_load_from_cache=lookup)}):
                self.assertTrue(server.adopt_legacy_file("model.safetensors", model, legacy_hub))
                # Already moved, not cached, or no old cache: nothing to adopt.
                self.assertFalse(server.adopt_legacy_file("model.safetensors", model, legacy_hub))
                self.assertFalse(server.adopt_legacy_file("config.json", model, legacy_hub))
                self.assertFalse(server.adopt_legacy_file("tokenizer.json", model, None))
            with open(os.path.join(model, "model.safetensors")) as moved:
                self.assertEqual(moved.read(), "weights")
            self.assertFalse(os.path.exists(os.path.join(snapshot, "model.safetensors")))

    def test_unavailable_cuda_is_reported_as_load_protocol_error(self):
        responses = []
        modules = {
            "torch": types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False), set_num_threads=lambda _: None),
            "soundfile": types.SimpleNamespace(),
            "transformers": types.SimpleNamespace(AutoModelForSpeechSeq2Seq=None, AutoProcessor=None),
        }
        with patch.dict(sys.modules, modules), patch.dict(server.os.environ, {"TYPEWHISPER_DEVICE": "NvidiaCuda"}), patch.object(sys, "stdin", io.StringIO('{"cmd":"load","req_id":42}\n')), patch.object(server, "respond", responses.append):
            server.cmd_serve()
        self.assertEqual(responses[-1]["req_id"], 42)
        self.assertIn("NVIDIA CUDA is unavailable", responses[-1]["error"])

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
        self.use_model_environment(TYPEWHISPER_MODEL_DIR="model-folder")
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

        def download(name, filename, revision, local_dir, tqdm_class):
            calls.append((filename, revision, local_dir))
            bar = tqdm_class(total=files[filename])
            bar.update(files[filename] // 2)
            bar.update(files[filename] - files[filename] // 2)

        modules = {
            "huggingface_hub": types.SimpleNamespace(HfApi=Api, hf_hub_download=download),
            "tqdm.auto": types.SimpleNamespace(tqdm=FakeBar),
        }
        with patch.dict(sys.modules, modules), patch.object(server, "respond", progress.append):
            server.cmd_setup()
        self.assertEqual([f for f, _, _ in calls], ["model.safetensors", "chat_template.jinja", "config.json"])
        self.assertTrue(all(revision == server.MODEL_REVISION and local_dir == "model-folder" for _, revision, local_dir in calls))
        self.assertAlmostEqual(progress[1]["progress"], 500 / 1015)
        self.assertEqual(progress[-1], {"progress": 1.0, "phase": "done"})


if __name__ == "__main__":
    unittest.main()
