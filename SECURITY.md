# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in TypeWhisper for Windows, please
report it responsibly.

**Do not open a public issue.** Instead, email security concerns to:
**security@typewhisper.com**

You can also use [GitHub's private vulnerability reporting](https://github.com/TypeWhisper/typewhisper-win/security/advisories/new).

We aim to acknowledge your report within 48 hours and to provide a fix within
7 days for critical issues.

When reporting, please include:

- Affected TypeWhisper version, commit, or release package
- Windows version and CPU architecture
- Clear reproduction steps
- Security impact and affected data or privileges
- Any relevant logs, screenshots, or proof-of-concept details that avoid
  exposing secrets or personal data

## Scope

TypeWhisper for Windows handles sensitive data including:

- Microphone audio and imported audio/video files
- Transcription history and generated text
- API keys and provider credentials
- Plugin catalog, plugin packages, plugin settings, and plugin-managed secrets
- Local HTTP API server
- Installer, update feed, and the Daily prerelease, Store beta, and stable channels

Issues in these areas are especially relevant.

## Security Boundaries

- The local HTTP API is off by default and must be enabled explicitly in
  Settings. It is intended for local tools and scripts, accepts loopback
  connections only, allows only literal local Host authorities (`127.0.0.1`,
  `[::1]`, or `localhost`, with the configured port), and rejects browser
  `Origin` headers. The Host allowlist also covers same-origin browser GETs
  without an Origin header after DNS rebinding.
- The API issues a token, but requests need it only when **Require API token**
  is enabled in Settings; see the [API reference](docs/WINUI-HTTP-API.md).
- The API accepts requests only from processes running as the same Windows user
  as TypeWhisper, so other user accounts on a shared machine cannot use it.
- API keys and plugin secrets are stored with Windows DPAPI, scoped to the
  current Windows user.
- Exported diagnostics and the JSON support report include app, platform,
  device, plugin, and error metadata, but should not include API keys, audio
  payloads, transcripts, or prompts.
- Plugins are executable code and are not sandboxed. Install them only from the
  TypeWhisper catalog or sources you trust.

## Supported Versions

| Version | Supported |
|---------|-----------|
| Latest release | Yes |
| Daily prerelease and Store beta | Best effort |
| Older versions | No |

## Research Guidelines

Good-faith security research is welcome. Please:

- Avoid accessing, modifying, or deleting data that is not yours
- Avoid exfiltrating secrets, API keys, recordings, transcripts, or personal data
- Avoid degrading service availability or disrupting other users
- Share only the information needed to reproduce and understand the issue
- Give the maintainers reasonable time to investigate before public disclosure

This project does not currently operate a paid bug bounty program.
