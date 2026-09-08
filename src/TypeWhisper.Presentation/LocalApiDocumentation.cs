using System.Text;

namespace TypeWhisper.Presentation;

internal static class LocalApiDocumentation
{
    internal static LocalApiResponse Response(int port) => new(200,
        Encoding.UTF8.GetBytes(Html.Replace("{{PORT}}", port.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        "text/html; charset=utf-8");

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>TypeWhisper HTTP API</title>
        <style>
        :root { color-scheme: dark; font: 16px/1.6 system-ui, sans-serif; background: #0b1119; color: #edf2f8; }
        body { max-width: 860px; margin: auto; padding: 32px 24px 64px; }
        h1,h2 { line-height: 1.25; } h2 { margin-top: 36px; }
        a { color: #53b9ff; } a:focus-visible { outline: 2px solid; outline-offset: 4px; }
        pre { padding: 16px; background: #151f2b; border-radius: 8px; overflow-x: auto; }
        code { font-size: .9em; } table { width: 100%; border-collapse: collapse; }
        th,td { text-align: left; padding: 12px 8px; border-bottom: 1px solid #304153; overflow-wrap: anywhere; }
        .muted { color: #afbdce; } nav { display: flex; flex-wrap: wrap; gap: 16px; }
        </style>
        <main>
        <p class="muted">TYPEWHISPER · API 1.1</p>
        <h1>Connect your scripts to TypeWhisper</h1>
        <p>Transcribe audio using the Dictation model or a request-scoped engine/model override. Keep TypeWhisper running with the HTTP API enabled under Settings → Advanced.</p>
        <nav aria-label="Contents"><a href="#start">Quick start</a><a href="#cli">CLI</a><a href="#endpoints">Endpoints</a><a href="#options">Options</a><a href="#discovery">Auto-discovery</a><a href="#errors">Errors</a></nav>
        <h2 id="start">Quick start</h2>
        <p>Base address: <code>http://127.0.0.1:{{PORT}}</code>. Copy your API token in Advanced settings. In PowerShell, replace <code>YOUR_API_TOKEN</code> below. Use <code>curl.exe</code> explicitly.</p>
        <pre><code>curl.exe http://127.0.0.1:{{PORT}}/v1/status
        $token = 'YOUR_API_TOKEN'
        curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:{{PORT}}/v1/models
        curl.exe -H "Authorization: Bearer $token" -F "file=@C:/Audio/sample.wav" http://127.0.0.1:{{PORT}}/v1/transcribe</code></pre>
        <p>Token authentication is optional. Leave <strong>Require API token</strong> off for the existing Raycast extension. When enabled, only GET <code>/docs</code>, <code>/docs/</code> and <code>/v1/status</code> are public; other requests need <code>Authorization: Bearer &lt;token&gt;</code> or <code>X-TypeWhisper-API-Token: &lt;token&gt;</code>. Never put the token in a URL. This page contains no credentials and sends no API requests.</p>
        <h2 id="cli">Command line tool</h2>
        <p>Install the CLI in Settings → Advanced → Command Line Tool, then open a new terminal. The CLI discovers the installing app's profile, API port and required token automatically. Keep the HTTP API enabled.</p>
        <pre><code>typewhisper status
        typewhisper models
        typewhisper last
        typewhisper history --limit 20
        typewhisper transcribe "C:\Audio\recording.wav"
        typewhisper transcribe "C:\Audio\recording.wav" --translate-to de
        typewhisper --help</code></pre>
        <p>Replace the audio path with your file. Target-language translation uses the default workflow LLM on Windows. Add <code>--json</code> for JSON output or <code>--no-corrections</code> to skip dictionary corrections. Commands that read status, models and history do not change your data.</p>
        <p><a href="https://www.typewhisper.com/en/docs/windows/cli/">Full CLI documentation and Mac comparison</a></p>
        <h2 id="endpoints">Endpoints</h2>
        <table><thead><tr><th scope="col">Request</th><th scope="col">Result</th></tr></thead><tbody>
        <tr><td>GET /v1/status</td><td>Server liveness and API version.</td></tr>
        <tr><td>GET /v1/models</td><td>Available models, active engine/model and readiness.</td></tr>
        <tr><td>GET /v1/capabilities</td><td>Supported endpoints, formats and limits.</td></tr>
        <tr><td>POST /v1/transcribe</td><td>Multipart file upload, or raw WAV/octet-stream body.</td></tr>
        <tr><td>POST /v1/transcribe/local-file</td><td>JSON body with an absolute local Windows path.</td></tr>
        </tbody></table>
        <h2>App control and data</h2>
        <table><thead><tr><th scope="col">Request</th><th scope="col">Usage</th></tr></thead><tbody>
        <tr><td>GET /v1/history<br>DELETE /v1/history</td><td>Search with q, limit (max 200) and offset. Delete one entry with query id; its saved audio is deleted too.</td></tr>
        <tr><td>GET /v1/profiles or /v1/rules<br>PUT /v1/profiles/toggle or /v1/rules/toggle</td><td>List workflow rules or toggle one with query id.</td></tr>
        <tr><td>POST /v1/dictation/start<br>POST /v1/dictation/stop<br>GET /v1/dictation/status<br>GET /v1/dictation/transcription</td><td>Start with optional JSON workflow_id. Stop returns a session id. Poll transcription with query id until completed or failed.</td></tr>
        <tr><td>POST /v1/recorder/start<br>POST /v1/recorder/stop<br>GET /v1/recorder/status<br>GET /v1/recorder/session</td><td>Optional start query mic and system_audio (true/false). Stop returns a session id; poll session with query id for the saved output_file.</td></tr>
        <tr><td>POST /v1/models/load<br>POST /v1/models/unload<br>DELETE /v1/models</td><td>Load JSON engine/model; unload JSON engine; delete query engine/model. Plugin capabilities and busy-state restrictions apply.</td></tr>
        <tr><td>GET /v1/dictionary/terms<br>PUT /v1/dictionary/terms<br>DELETE /v1/dictionary/terms</td><td>PUT JSON terms array or term_entries, with optional replace. DELETE JSON term.</td></tr>
        <tr><td>GET /v1/dictionary/corrections<br>PUT /v1/dictionary/corrections<br>DELETE /v1/dictionary/corrections</td><td>PUT JSON original, replacement and optional caseSensitive. DELETE JSON original.</td></tr>
        <tr><td>GET /v1/settings/export<br>POST /v1/settings/import</td><td>Export a Windows portable profile backup, or POST one as JSON. A changed import returns 202/restoring, then drains the app and restarts to apply it. Invalid backups return 400. These archives contain supported dictionary, snippets, workflows and History data; credentials and device settings are excluded.</td></tr>
        </tbody></table>
        <p>Raycast: keep the default port 8978, or set its API Port Override. The existing extension does not read Windows discovery files. Leave token authentication off; no extension update is needed.</p>
        <p>Example JSON body for a local file:</p>
        <pre><code>{"path":"C:/Audio/sample.wav","task":"transcribe","response_format":"json"}</code></pre>
        <h2 id="options">Transcription options</h2>
        <p>Use multipart fields, query parameters for raw uploads, or JSON fields for local files.</p>
        <ul><li><code>language</code>: a supported language code, such as <code>en</code> or <code>de</code>. Defaults to the Dictation setting.</li>
        <li><code>task</code>: <code>transcribe</code> or <code>translate</code>, where supported. Defaults to the Dictation setting.</li>
        <li><code>response_format</code>: <code>json</code> (default), <code>text</code>, <code>srt</code> or <code>vtt</code>. Subtitles require provider timestamps.</li>
        <li><code>model</code> and <code>engine</code>: request-scoped overrides from GET /v1/models. The backend loads the requested model and restores the previous selection afterward. A loadable override also works when no model is currently ready. Portable plugin providers require an initial selection to restore afterward; otherwise the request returns 409.</li>
        <li><code>language_hint</code>: repeat multipart fields, or use a JSON <code>language_hints</code> array. At most two ordered language codes; cannot be combined with <code>language</code>. Requires a provider that supports hints.</li>
        <li><code>await_download</code>: boolean, commonly query <code>?await_download=1</code>. Allows required model assets to download before loading; otherwise assets must already be available.</li>
        <li><code>apply_corrections</code>: boolean, default <code>true</code>. Set <code>false</code> to bypass dictionary corrections. File API requests do not run snippets or post-processors.</li>
        <li><code>target_language</code>: translate through the Windows default workflow LLM configuration. Missing default LLM configuration returns 422. This does not use the macOS Translation framework.</li></ul>
        <p>JSON boolean fields require actual booleans. Multipart/query booleans accept true/false, 1/0, yes/no and on/off. Unknown options, including prompt and normalize_numbers, are rejected. Repeated multipart language_hint is allowed; other duplicate options are rejected.</p>
        <p>JSON results contain <code>text</code>, <code>language</code> (actual output language), <code>engine</code>, <code>model</code>, <code>duration</code>, <code>warnings</code> and <code>segments</code> with text/start/end. Unknown and duplicate options are rejected.</p>
        <h2 id="discovery">Auto-discovery</h2>
        <p>The active profile contains <code>api-discovery.json</code> (base_url, port, token and process information) and <code>api-port</code>. The normal development profile is <code>%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData</code>. The discovery token is accessible only to your Windows user. Files are removed when the API stops; verify liveness after an unexpected app exit.</p>
        <h2 id="errors">Limits and errors</h2>
        <p>Uploads: up to 32 MiB including multipart framing. Audio: up to 60 minutes. Local-file paths cannot use network shares or reparse points. Only loopback connections are accepted; browser-origin API requests are blocked.</p>
        <table><thead><tr><th scope="col">Status</th><th scope="col">Meaning</th></tr></thead><tbody>
        <tr><td>400</td><td>Invalid, duplicate or unsupported options.</td></tr><tr><td>401 / 403</td><td>Missing/invalid token or disallowed request origin.</td></tr>
        <tr><td>409</td><td>Engine busy, model operation unavailable or required model assets missing.</td></tr><tr><td>413 / 422</td><td>Upload too large, invalid audio, unsupported language/task/hints, missing translation configuration or missing subtitle timestamps.</td></tr>
        <tr><td>429 / 503</td><td>Too many concurrent requests or no model ready.</td></tr></tbody></table>
        <p>File-transcription requests do not paste text or save History/audio. Dictation and recorder control use the same recording and output settings as the app. File API requests apply dictionary corrections by default, with no snippets or post-processors. Cloud transcription and requested LLM translation make their normal provider calls.</p>
        </main></html>
        """;
}
