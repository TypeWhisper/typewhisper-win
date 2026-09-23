"""Release metadata must pass the same catalog client as the shipped app."""
import json
import pathlib
import subprocess
import tempfile
import unittest


class HostSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        project = pathlib.Path(__file__).parents[1] / 'PluginCatalogVerifier/PluginCatalogVerifier.csproj'
        try:
            result = subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '-v', 'quiet'],
                                    capture_output=True, text=True, timeout=300)
        except subprocess.TimeoutExpired as error:
            raise AssertionError('Catalog verifier build timed out after 300 seconds') from error
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.verifier = project.parent / 'bin/Release/net10.0/PluginCatalogVerifier.dll'

    def run_verifier(self, document):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'feed.json'
            path.write_text(json.dumps(document), encoding='utf-8')
            try:
                return subprocess.run(['dotnet', str(self.verifier), str(path)], capture_output=True, timeout=30).returncode
            except subprocess.TimeoutExpired as error:
                raise AssertionError('Catalog verifier invocation timed out after 30 seconds') from error

    def entry(self):
        return dict(id='com.typewhisper.example', name='Example', version='1.0.0', minHostVersion='1.1.0',
                    sha256='a' * 64, size=100, downloadUrl='https://example.com/plugin.zip',
                    categories=['memory'], platforms=['windows'], supportedArchitectures=['x64'])

    def test_valid_array_and_object_with_default_minimum(self):
        self.assertEqual(0, self.run_verifier([self.entry()]))
        entry = self.entry()
        entry.pop('minHostVersion')
        self.assertEqual(0, self.run_verifier({'plugins': [entry]}))

    def test_host_rejects_invalid_metadata_and_duplicates(self):
        mutations = [dict(categories=['all']), dict(categories=[' ']), dict(categories=None), dict(id='../bad'),
                     dict(name=' '), dict(version='invalid'), dict(minHostVersion='invalid'),
                     dict(sha256='bad'), dict(size=0), dict(size=2**50), dict(downloadUrl='http://example.com/a.zip')]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                self.assertNotEqual(0, self.run_verifier({'plugins': [self.entry() | mutation]}))
        self.assertNotEqual(0, self.run_verifier([self.entry(), self.entry()]))


if __name__ == '__main__':
    unittest.main()
