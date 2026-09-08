"""Test Mac-compatible CLI options against a running isolated local-model profile."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--audio', type=Path, required=True)
    args = parser.parse_args()
    profile = args.profile.resolve(strict=True)
    root = (Path(tempfile.gettempdir()) / 'TypeWhisper-WinUI-TestProfiles').resolve()
    assert profile != root and profile.is_relative_to(root), 'Use an isolated test profile.'
    discovery = json.loads((profile / 'api-discovery.json').read_text(encoding='utf-8-sig'))
    env = os.environ.copy()
    env.pop('TYPEWHISPER_API_TOKEN', None)
    env.pop('TYPEWHISPER_PROFILE', None)
    checks = []
    def cli(*command, stdin=None, expected=0):
        result = subprocess.run([str(args.cli.resolve()), *command, '--profile', str(profile), '--json'],
            input=stdin, capture_output=True, env=env, timeout=180)
        assert result.returncode == expected, f'{command[0]} returned {result.returncode}, expected {expected}'
        checks.append(command[0])
        return json.loads(result.stdout) if result.stdout.strip() else None
    def request(method, body=None):
        headers = {'Content-Type': 'application/json'}
        if discovery.get('requires_authentication'):
            headers['Authorization'] = 'Bearer ' + discovery['token']
        req = urllib.request.Request(f"http://127.0.0.1:{discovery['port']}/v1/dictionary/corrections",
            data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=10) as response:
            return json.load(response)
    audio = args.audio.resolve(strict=True)
    before = cli('status')
    if discovery.get('requires_authentication'):
        cli('history', '--api-token', 'synthetic-wrong-token', expected=3)
    assert before['engine'] == 'sherpa-onnx' and before['model'] == 'canary-180m-flash'
    raw = cli('transcribe', str(audio), '--no-corrections')['text']
    assert 'Tomorrow' in raw
    assert not any(item['original'].lower() == 'tomorrow' for item in request('GET')['corrections'])
    try:
        request('PUT', {'original': 'Tomorrow', 'replacement': 'CLI_CORRECTION_FIXTURE', 'caseSensitive': True})
        corrected = cli('transcribe', str(audio))['text']
        assert 'CLI_CORRECTION_FIXTURE' in corrected
        assert cli('transcribe', str(audio), '--no-corrections')['text'] == raw
        assert cli('transcribe', '--no-corrections', stdin=audio.read_bytes())['text'] == raw
        cli('transcribe', str(audio), '--engine', 'missing-fixture-engine', expected=3)
        assert cli('status')['model'] == before['model']
        cli('transcribe', str(audio), '--language-hint', 'de', expected=3)
        # This fixture intentionally has no LLM, so translation must fail explicitly.
        assert not (profile / 'workflow-llm-default.json').exists()
        cli('transcribe', str(audio), '--translate-to', 'de', expected=3)
        cli('models', 'unload', '--engine', before['engine'])
        try:
            result = cli('transcribe', str(audio), '--model', 'local:canary-180m-flash', '--await-download', '--no-corrections')
            assert result['model'] == before['model'] and result['text'] == raw
            assert cli('status')['status'] == 'no_model', 'Request override must restore unloaded state.'
        finally:
            cli('models', 'load', '--engine', before['engine'], '--model', before['model'])
    finally:
        request('DELETE', {'original': 'Tomorrow'})
    Path('artifacts/cli-options-native-evidence.json').write_text(json.dumps({
        'passed': True, 'checks': len(checks), 'authentication_required': discovery.get('requires_authentication', False),
        'correction_opt_out': True, 'implicit_stdin': True, 'model_override_restored': True,
        'translation_missing_provider_rejected': True, 'unsupported_hints_rejected': True}, indent=2))
    print(json.dumps({'passed': True, 'checks': len(checks)}))


if __name__ == '__main__':
    main()
