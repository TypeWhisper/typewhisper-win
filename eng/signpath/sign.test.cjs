const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const childProcess = require('node:child_process');
const vm = require('node:vm');
const { actionOutput, allowedExecutable } = require('./sign.cjs');

test('workflow selects the signing environment and omits unused token inputs', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../../.github/workflows/winui-daily-candidate.yml'), 'utf8');
  const inputs = [...workflow.matchAll(/^\s*api-token:\s*\$\{\{ (.+) \}\}\s*$/gm)];
  assert.equal(inputs.length, 1, 'Expected one conditional signing-token input');
  const environments = [...workflow.matchAll(/^    environment:\s*\$\{\{ (.+) \}\}\s*$/gm)];
  assert.equal(environments.length, 1, 'Expected one environment on the packaging job');
  // This expression uses only property access, equality and boolean operators,
  // which have the same result in Actions and JavaScript for these string inputs.
  const expression = new vm.Script(inputs[0][1]);
  const environmentExpression = new vm.Script(environments[0][1]);
  for (const [event, allowedPolicies] of [
    ['pull_request', []],
    ['pull_request_target', []],
    ['push', []],
    ['workflow_dispatch', ['test-signing', 'release-signing']],
    ['schedule', ['test-signing', 'release-signing']],
  ]) {
    // These routing checks are defense in depth. The actual trust boundary is
    // GitHub's environment branch policy and the absence of a repository token.
    for (const policy of ['none', 'test-signing', 'release-signing', 'unknown', '']) {
      for (const token of ['signing-token-fixture', '']) {
        const context = {
          github: { event_name: event },
          needs: { version: { outputs: { signing_policy: policy } } },
          secrets: { SIGNPATH_API_TOKEN: token },
        };
        const actual = expression.runInNewContext(context, { timeout: 1000 });
        assert.equal(actual, allowedPolicies.includes(policy) ? token : '', `${event}: ${policy}`);
        const environment = environmentExpression.runInNewContext(context, { timeout: 1000 });
        assert.equal(environment, policy === 'none' ? 'unsigned-candidate' : 'signpath', `${event}: ${policy}`);
      }
    }
  }
});

test('read GitHub action output delimiters and reject incomplete outputs', () => {
  assert.equal(actionOutput('artifact-id<<unique\r\n123\r\nunique\r\n', 'artifact-id'), '123');
  assert.equal(actionOutput('other=value\nartifact-id=456\n', 'artifact-id'), '456');
  assert.throws(() => actionOutput('artifact-id<<unique\n123', 'artifact-id'), /Unterminated/);
  assert.throws(() => actionOutput('other=123', 'artifact-id'), /Missing/);
});

test('allow application, CLI and generated helpers without signing unrelated binaries', () => {
  for (const name of ['TypeWhisper.exe', 'typewhisper.exe', 'TypeWhisper_ExecutionStub.exe',
    'TypeWhisperDaily-win-x64-winui-daily-Setup.exe', 'Squirrel.exe', 'Update.exe']) {
    assert.equal(allowedExecutable(name), true, name);
  }
  for (const name of ['Microsoft.UI.Xaml.dll', 'TypeWhisper.dll', 'unrelated.exe', 'TypeWhisper.exe.ps1']) {
    assert.equal(allowedExecutable(name), false, name);
  }
});

for (const failure of ['upload', 'sign', 'verify', null]) {
  test(`signing ${failure ? `failure at ${failure} preserves originals` : 'replaces files only after verification'}`, t => {
    const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'signpath-test-'));
    t.after(() => fs.rmSync(temporary, { recursive: true, force: true }));
    const original = path.join(temporary, 'TypeWhisper.exe');
    fs.writeFileSync(original, 'original');
    const calls = [];
    t.mock.method(childProcess, 'spawnSync', (command, args, options) => {
      const stage = command === 'pwsh' ? 'verify'
        : args[0].includes(`${path.sep}upload${path.sep}`) ? 'upload' : 'sign';
      calls.push(stage);
      assert.equal(fs.readFileSync(original, 'utf8'), 'original');
      if (stage !== 'verify') {
        assert.equal(options.env.INPUT_SCRIPT, undefined);
        assert.equal(fs.readFileSync(options.env.GITHUB_OUTPUT, 'utf8'), '');
      }
      if (stage === failure) return { status: 1 };
      if (stage === 'upload') {
        assert.equal(options.env.INPUT_ARCHIVE, 'true');
        fs.writeFileSync(options.env.GITHUB_OUTPUT, 'artifact-id<<id\n42\nid\n');
      } else if (stage === 'sign') {
        assert.equal(options.env['INPUT_GITHUB-ARTIFACT-ID'], '42');
        assert.equal(options.env['INPUT_ARTIFACT-CONFIGURATION-SLUG'], 'github-executables');
        const target = path.join(options.env['INPUT_OUTPUT-ARTIFACT-DIRECTORY'], '0', 'TypeWhisper.exe');
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, 'signed');
        fs.writeFileSync(options.env.GITHUB_OUTPUT, 'signing-request-web-url=https://app.signpath.io/request/42\n');
      }
      return { status: 0 };
    });
    delete require.cache[require.resolve('./sign.cjs')];
    const { sign } = require('./sign.cjs');
    const env = { GITHUB_WORKSPACE: temporary, RUNNER_TEMP: temporary, SIGNPATH_POLICY: 'test-signing',
      SIGNPATH_API_TOKEN: 'test-fixture', SIGNPATH_GITHUB_TOKEN: 'test-fixture',
      SIGNPATH_CERTIFICATE_THUMBPRINT: '0'.repeat(40), INPUT_SCRIPT: 'must not reach nested actions' };
    if (failure) {
      assert.throws(() => sign([original], env), /failed/i);
      assert.equal(fs.readFileSync(original, 'utf8'), 'original');
    } else {
      sign([original], env);
      assert.equal(fs.readFileSync(original, 'utf8'), 'signed');
    }
    assert.deepEqual(calls, ['upload', 'sign', 'verify'].slice(0, failure === 'upload' ? 1 : failure === 'sign' ? 2 : 3));
  });
}

for (const [event, ref, policy, expected] of [
  ['pull_request', 'refs/pull/1/merge', 'test-signing', /never pull requests/],
  ['workflow_dispatch', 'refs/heads/feature', 'release-signing', /restricted to main/],
  ['workflow_dispatch', 'refs/heads/feature', 'test-signing', /restricted to main/],
  ['schedule', 'refs/heads/main', 'unknown', /Invalid signing policy/],
]) {
  test(`reject ${policy} from ${event} on ${ref}`, () => {
    const result = childProcess.spawnSync(process.execPath, [path.join(__dirname, 'run.cjs')], {
      encoding: 'utf8', env: { ...process.env, INPUT_SCRIPT: 'throw "must not execute"',
        INPUT_POLICY: policy, GITHUB_EVENT_NAME: event, GITHUB_REF: ref },
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, expected);
  });
}
