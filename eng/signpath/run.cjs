const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const policy = process.env.INPUT_POLICY;
const script = process.env.INPUT_SCRIPT;
let temporary;
try {
  if (!['none', 'test-signing', 'release-signing'].includes(policy)) throw new Error('Invalid signing policy.');
  if (!script) throw new Error('Packaging script is required.');
  const env = { ...process.env, SIGNPATH_POLICY: policy };
  if (policy !== 'none') {
    if (env.GITHUB_EVENT_NAME !== 'workflow_dispatch' && env.GITHUB_EVENT_NAME !== 'schedule') {
      throw new Error('Signing is limited to explicit or scheduled workflows, never pull requests.');
    }
    if (env.GITHUB_REF !== 'refs/heads/main') {
      throw new Error('Signing is restricted to main through the signpath environment.');
    }
    for (const key of ['ACTIONS_RUNTIME_TOKEN', 'ACTIONS_RESULTS_URL', 'SIGNPATH_GITHUB_TOKEN']) {
      if (!env[key]) throw new Error(`Missing ${key}.`);
    }
    env.SIGNPATH_API_TOKEN = env['INPUT_API-TOKEN'];
    if (!env.SIGNPATH_API_TOKEN) throw new Error('Configure SIGNPATH_API_TOKEN in the protected signpath environment.');
    env.SIGNPATH_CERTIFICATE_THUMBPRINT = policy === 'test-signing'
      ? '7E9AEA1B2BD953BCBD6C5241E45C3210D86137B1'
      : env['INPUT_CERTIFICATE-THUMBPRINT'];
    if (!/^[A-Fa-f0-9]{40}$/.test(env.SIGNPATH_CERTIFICATE_THUMBPRINT || '')) {
      throw new Error('Configure SIGNPATH_CERTIFICATE_THUMBPRINT before production signing.');
    }
    env.SIGNPATH_NODE = process.execPath;
    env.SIGNPATH_HELPER = path.join(__dirname, 'sign.cjs');
    env.SIGNPATH_SIGN_TEMPLATE = `"${process.execPath}" "${env.SIGNPATH_HELPER}" {{file...}}`;
    // JavaScript actions receive the artifact-service credentials. Keeping the
    // packaging process inside this action avoids exporting them to other steps.
  }
  temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'signpath-run-'));
  const scriptPath = path.join(temporary, 'package.ps1');
  fs.writeFileSync(scriptPath, `$ErrorActionPreference = 'Stop'\n${script}\n`, 'utf8');
  const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', scriptPath], {
    env, stdio: 'inherit', cwd: process.env.GITHUB_WORKSPACE,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Packaging failed (exit ${result.status}).`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
} finally {
  if (temporary) fs.rmSync(temporary, { recursive: true, force: true });
}
