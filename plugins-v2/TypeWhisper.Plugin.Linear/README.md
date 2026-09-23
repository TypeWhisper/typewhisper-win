# Linear

Portable workflow action `create-linear-issue`, plugin version 1.2.2, minimum host 1.1.5.

## Setup and behavior

1. Save a personal Linear API key in the plugin settings.
2. Select **Load teams and projects** to fetch the available choices. The request is read-only and follows Linear pagination.
3. Select a team and, optionally, a project belonging to that team. Save the settings together.
4. Add **Create Linear issue** to a workflow. The first non-empty input line supplies the title (up to 100 characters); the complete input becomes the Markdown description.

The connection test reads the current user and never creates an issue. Only the workflow action creates an issue. Successful results include the verified `https://linear.app` issue link.

The settings use the host's shared save UI and Linear brand icon. Team/project names are cached for restart and tied to the saved API key fingerprint; changing the key hides the prior account's cached names. Failed or canceled refreshes retain the previous list. Project choices include all accessible projects; choose one associated with the selected team. Linear validates the final team/project combination.

Personal API keys use the raw Authorization header. Keys remain in the encrypted host secret store, and are never exposed in settings or logs. HTTP redirects are disabled. GraphQL errors, incomplete responses, cancellation, and provider failures are surfaced without creating a success result.

## Validation

`dotnet test plugins-v2/TypeWhisper.Plugin.Linear/Tests/TypeWhisper.Plugin.Linear.Portable.Tests.csproj -c Release`

24 tests cover package lifecycle, key persistence, failed saves, settings, issue requests, response validation, pagination, cached choices, account changes, and canceled/failed refreshes. A read-only live check with the existing development-profile key succeeded and loaded two teams and one project. No live issue was created during automated verification; the complete workflow remains a manual acceptance step.

The macOS Linear plugin was compared for action behavior and team selection. The Windows implementation adds an optional project selector and uses the shared WinUI settings host.

References: [Linear GraphQL](https://linear.app/developers/graphql), [pagination](https://linear.app/developers/pagination).

## Workflow action target

In Workflows, create or edit a workflow and select **Action Target → Create Linear issue**.
The host discovers actions from enabled plugins; the selected action receives the final text after
transcription and workflow processing, instead of pasting it. The Linear settings supply the team
and optional project. A dictation-only template can send the transcript without an LLM.
Manual runs and selected-text shortcuts use the same destination. Missing actions and failed or
uncertain requests preserve the text for review and are never automatically retried.
