const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

function runNodeAction(entry, inputs, outputFile, env) {
  // Action defaults normally come from action.yml; nested invocations set all
  // required defaults explicitly and isolate their GITHUB_OUTPUT file.
  const childEnv = Object.fromEntries(Object.entries(env).filter(([key]) => !key.startsWith('INPUT_')));
  for (const [name, value] of Object.entries(inputs)) childEnv[`INPUT_${name.toUpperCase()}`] = String(value);
  childEnv.GITHUB_OUTPUT = outputFile;
  // The Actions toolkit requires the runner-provided output file to exist.
  fs.writeFileSync(outputFile, '', { flag: 'wx' });
  const result = spawnSync(process.execPath, [entry], { env: childEnv, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Signing action failed: ${path.basename(path.dirname(entry))} (exit ${result.status}).`);
  return fs.existsSync(outputFile) ? fs.readFileSync(outputFile, 'utf8') : '';
}

function actionOutput(text, name) {
  const lines = text.replace(/\r/g, '').split('\n');
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].startsWith(`${name}=`)) return lines[i].slice(name.length + 1);
    if (lines[i].startsWith(`${name}<<`)) {
      const delimiter = lines[i].slice(name.length + 2);
      const end = lines.indexOf(delimiter, i + 1);
      if (end < 0) throw new Error(`Unterminated action output ${name}.`);
      return lines.slice(i + 1, end).join('\n');
    }
  }
  throw new Error(`Missing action output ${name}.`);
}

function allowedExecutable(file) {
  return /^(?:TypeWhisper[^\\/]*|Squirrel|Update)\.exe$/i.test(path.basename(file));
}

function sign(files, env = process.env) {
  if (!files.length || files.some(file => !allowedExecutable(file) || !fs.statSync(file).isFile())) {
    throw new Error('Only TypeWhisper and Velopack executables may be submitted for signing.');
  }
  if (!['test-signing', 'release-signing'].includes(env.SIGNPATH_POLICY)) throw new Error('Invalid signing policy.');
  const workspace = env.GITHUB_WORKSPACE;
  if (!workspace || !env.SIGNPATH_API_TOKEN || !env.SIGNPATH_GITHUB_TOKEN) throw new Error('Missing signing context.');
  const temporary = fs.mkdtempSync(path.join(env.RUNNER_TEMP || os.tmpdir(), 'signpath-files-'));
  try {
    const input = path.join(temporary, 'unsigned');
    const output = path.join(temporary, 'signed');
    const mappings = files.map((file, index) => {
      const relative = path.join(String(index), path.basename(file));
      const staged = path.join(input, relative);
      fs.mkdirSync(path.dirname(staged), { recursive: true });
      fs.copyFileSync(file, staged);
      return { original: path.resolve(file), signed: path.join(output, relative) };
    });
    const uploadOutput = runNodeAction(path.join(workspace, '.signpath-tools/upload/dist/upload/index.js'), {
      name: `signpath-${env.GITHUB_JOB}-${env.GITHUB_RUN_ATTEMPT}-${crypto.randomUUID()}`,
      path: input, 'if-no-files-found': 'error', 'retention-days': '7',
      'compression-level': '6', overwrite: 'false', 'include-hidden-files': 'false', archive: 'true',
    }, path.join(temporary, 'upload-output'), env);
    const artifactId = actionOutput(uploadOutput, 'artifact-id');
    if (!/^\d+$/.test(artifactId)) throw new Error('Invalid GitHub artifact ID.');
    const signingOutput = runNodeAction(path.join(workspace, '.signpath-tools/submit/index.js'), {
      'connector-url': 'https://pipelineconnector.connectors.signpath.io/GitHubActions/GitHubCom',
      'api-token': env.SIGNPATH_API_TOKEN,
      'organization-id': '855e873b-1597-4361-b8d9-2d871b7c7d0c',
      'project-slug': 'typewhisper-win', 'artifact-configuration-slug': 'github-executables',
      'signing-policy-slug': env.SIGNPATH_POLICY, 'github-artifact-id': artifactId,
      'github-token': env.SIGNPATH_GITHUB_TOKEN, 'wait-for-completion': 'true',
      'output-artifact-directory': output, 'skip-decompress': 'false',
      'wait-for-completion-timeout-in-seconds': '1800',
      'service-unavailable-timeout-in-seconds': '600', 'download-signed-artifact-timeout-in-seconds': '300',
    }, path.join(temporary, 'sign-output'), env);
    const manifest = path.join(temporary, 'files.json');
    fs.writeFileSync(manifest, JSON.stringify(mappings.map(mapping => mapping.signed)));
    const verification = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File',
      path.join(__dirname, 'Verify-Signatures.ps1'), '-FilesJson', manifest,
      '-Policy', env.SIGNPATH_POLICY, '-Thumbprint', env.SIGNPATH_CERTIFICATE_THUMBPRINT],
    { stdio: 'inherit', env });
    if (verification.error) throw verification.error;
    if (verification.status !== 0) throw new Error('Signed artifact verification failed; originals were not replaced.');
    for (const mapping of mappings) fs.copyFileSync(mapping.signed, mapping.original);
    if (env.GITHUB_STEP_SUMMARY) {
      const url = actionOutput(signingOutput, 'signing-request-web-url');
      if (!url.startsWith('https://app.signpath.io/')) throw new Error('Unexpected signing request URL.');
      fs.appendFileSync(env.GITHUB_STEP_SUMMARY,
        `- [SignPath request](${url}): ${files.length} executable(s), \`${env.SIGNPATH_POLICY}\`\n`);
    }
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

if (require.main === module) {
  try { sign(process.argv.slice(2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { actionOutput, allowedExecutable, sign };
