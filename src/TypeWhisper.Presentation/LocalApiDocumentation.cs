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
        <p>Transcribe audio using the model selected in Dictation. Keep TypeWhisper running with the HTTP API enabled under Settings → Advanced.</p>
        <nav aria-label="Contents"><a href="#start">Quick start</a><a href="#endpoints">Endpoints</a><a href="#options">Options</a><a href="#discovery">Auto-discovery</a><a href="#errors">Errors</a></nav>
        <h2 id="start">Quick start</h2>
        <p>Base address: <code>http://127.0.0.1:{{PORT}}</code>. Copy your API token in Advanced settings. In PowerShell, replace <code>YOUR_API_TOKEN</code> below. Use <code>curl.exe</code> explicitly.</p>
        <pre><code>curl.exe http://127.0.0.1:{{PORT}}/v1/status
        $token = 'YOUR_API_TOKEN'
        curl.exe -H "Authorization: Bearer $token" http://127.0.0.1:{{PORT}}/v1/models
        curl.exe -H "Authorization: Bearer $token" -F "file=@C:/Audio/sample.wav" http://127.0.0.1:{{PORT}}/v1/transcribe</code></pre>
        <p>Only GET <code>/docs</code>, <code>/docs/</code> and <code>/v1/status</code> are public. Other requests need <code>Authorization: Bearer &lt;token&gt;</code> or <code>X-TypeWhisper-API-Token: &lt;token&gt;</code>. Never put the token in a URL. This page contains no credentials and sends no API requests.</p>
        <h2 id="endpoints">Endpoints</h2>
        <table><thead><tr><th scope="col">Request</th><th scope="col">Result</th></tr></thead><tbody>
        <tr><td>GET /v1/status</td><td>Server liveness and API version.</td></tr>
        <tr><td>GET /v1/models</td><td>Available models, active engine/model and readiness.</td></tr>
        <tr><td>GET /v1/capabilities</td><td>Supported endpoints, formats and limits.</td></tr>
        <tr><td>POST /v1/transcribe</td><td>Multipart file upload, or raw WAV/octet-stream body.</td></tr>
        <tr><td>POST /v1/transcribe/local-file</td><td>JSON body with an absolute local Windows path.</td></tr>
        </tbody></table>
        <p>Example JSON body for a local file:</p>
        <pre><code>{"path":"C:/Audio/sample.wav","task":"transcribe","response_format":"json"}</code></pre>
        <h2 id="options">Transcription options</h2>
        <p>Use multipart fields, query parameters for raw uploads, or JSON fields for local files.</p>
        <ul><li><code>language</code>: a supported language code, such as <code>en</code> or <code>de</code>. Defaults to the Dictation setting.</li>
        <li><code>task</code>: <code>transcribe</code> or <code>translate</code>, where supported. Defaults to the Dictation setting.</li>
        <li><code>response_format</code>: <code>json</code> (default), <code>text</code>, <code>srt</code> or <code>vtt</code>. Subtitles require provider timestamps.</li>
        <li><code>model</code> and <code>engine</code>: optional checks against the selected model. These never switch models.</li></ul>
        <p>JSON results contain <code>text</code>, <code>engine</code>, <code>model</code>, <code>duration</code>, <code>warnings</code> and <code>segments</code> with text/start/end. Unknown and duplicate options are rejected.</p>
        <h2 id="discovery">Auto-discovery</h2>
        <p>The active profile contains <code>api-discovery.json</code> (base_url, port, token and process information) and <code>api-port</code>. The normal development profile is <code>%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData</code>. The discovery token is accessible only to your Windows user. Files are removed when the API stops; verify liveness after an unexpected app exit.</p>
        <h2 id="errors">Limits and errors</h2>
        <p>Uploads: up to 32 MiB including multipart framing. Audio: up to 60 minutes. Local-file paths cannot use network shares or reparse points. Only loopback connections are accepted; browser-origin API requests are blocked.</p>
        <table><thead><tr><th scope="col">Status</th><th scope="col">Meaning</th></tr></thead><tbody>
        <tr><td>400</td><td>Invalid, duplicate or unsupported options.</td></tr><tr><td>401 / 403</td><td>Missing/invalid token or disallowed request origin.</td></tr>
        <tr><td>409</td><td>Engine busy or selected model does not match.</td></tr><tr><td>413 / 422</td><td>Upload too large, invalid audio, unsupported language/task or missing subtitle timestamps.</td></tr>
        <tr><td>429 / 503</td><td>Too many concurrent requests or no model ready.</td></tr></tbody></table>
        <p>The API does not paste text, save History/audio, or control dictation and the recorder. Your configured vocabulary and text-processing pipeline still apply. Cloud models and processing plugins make their normal provider calls.</p>
        </main></html>
        """;
}
