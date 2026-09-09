# Windows meeting automation: Mac reference and calendar setup

## Implementation status

Reference: macOS `5f04ae3d1910a252748ba53f26a6e13d5cf18851`, specifically `CalendarMeetingAutomationPolicy.swift`, `MeetingLinkParser.swift`, `EventKitCalendarMeetingProvider.swift`, and their tests.

The portable Windows preparation contains `CalendarMeetingClient` and `MeetingLink`. The adapters read one selected Microsoft or Google calendar, expand recurring events, follow bounded pagination, normalize times, and omit all-day, canceled, declined, invalid and non-meeting events. Zoom, Teams and Google Meet links use the Mac canonical identities. Query passcodes and tracking parameters are not retained. FaceTime parsing is not ported yet. No calendar data is persisted by these adapters.

These are tested building blocks, not a live calendar connection. There are no OAuth registrations or real-account validation yet. The Premium UI still correctly marks meeting automation unavailable. OAuth/token-cache integration, calendar selection, reminders, recorder lifecycle wiring, countdowns and automatic stopping remain to implement. Do not enable the feature based solely on these API adapters.

## Mac behavior to retain

- Off, reminder and automatic modes, selected calendars and enabled meeting providers.
- Commercial license or verified Premium entitlement required.
- Browser meeting identity must match a calendar occurrence inside the join window (10 minutes before start through 30 minutes after end).
- A provider homepage is not evidence of joining. Mac combines meeting identity with attributed audio activity or its camera fallback; only input activity or camera presence permits starting.
- Pending/declined/unknown participation must not silently trigger recording. Preserve recorder/dictation busy handling, per-occurrence suppression and recording ownership.
- Mac waits for a stable join signal for 3 seconds, then displays a cancelable 5-second start countdown. Port the complete policy and tests before enabling automatic start/stop.

Windows already reads browser-owned address bars for Edge, Chrome/Chromium, Brave and Firefox via `WindowsBrowserTargetReader`. It currently returns only a hostname and excludes page content. Exact meeting recognition needs a separate opt-in path that canonicalizes a supported meeting link before discarding the raw address. Keep the existing hostname-only workflow behavior. Do not treat a successful URL probe as proof of audio participation, or missing probe data as proof a meeting ended.

## Microsoft registration (one-time product setup)

1. Register **TypeWhisper** in Microsoft Entra → App registrations.
2. Support both organizational directories and personal Microsoft accounts, so Microsoft 365 and Outlook.com users can connect.
3. Add the **Mobile and desktop applications** platform with `http://localhost` for system-browser MSAL authentication.
4. Add delegated Microsoft Graph **Calendars.Read**. Meeting links may occur in event bodies, so `Calendars.ReadBasic` is insufficient for the full Mac behavior. No calendar write permission or application-wide mailbox access is needed.
5. Record the public Application (client) ID. Do not create or embed a confidential-client secret for this desktop flow.
6. Use MSAL with a protected per-profile token cache, silent refresh, interactive reconnect when required, cancellation and explicit disconnect. Tenant consent policies may require an administrator.

Microsoft references: [desktop registration](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-registration), [MSAL browser flow](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers), [calendar view](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0).

## Google registration (one-time product setup)

1. Create/select the TypeWhisper Google Cloud project and enable **Google Calendar API**.
2. Configure Google Auth Platform branding, audience and contact information. Use an external audience for a public product; add test users while testing.
3. Create an OAuth client of type **Desktop app**. Record its public client ID and keep the downloaded configuration out of source control until its fields have been reviewed. A desktop client cannot keep an embedded secret confidential.
4. Request read-only scopes: `https://www.googleapis.com/auth/calendar.calendarlist.readonly` for calendar selection and `https://www.googleapis.com/auth/calendar.events.readonly` for event data. Do not request write access.
5. Use the system browser and supported loopback redirect with PKCE. Store refresh tokens protected for the current Windows user and app profile. Disconnect must remove the local token cache.
6. Complete Google's applicable consent-screen verification before public release. Test-mode access/refresh-token lifetime is not a production-readiness test.

Google references: [desktop OAuth](https://developers.google.com/identity/protocols/oauth2/native-app), [calendar authorization scopes](https://developers.google.com/workspace/calendar/api/auth), [event instances and pagination](https://developers.google.com/workspace/calendar/api/v3/reference/events/list).

## Adapter integration contract

Supply an `HttpClient` with automatic redirects disabled and a timeout, plus a token-acquisition callback belonging to the selected account. A complete snapshot is returned only after all pages succeed; never replace a valid prior snapshot with an empty list on HTTP, cancellation or pagination failure. Microsoft continuation URLs must remain on the exact original calendar-view path and origin; Google continuation tokens are escaped into the original endpoint. Page count and response size are bounded.

Request a rolling time window and selected calendar IDs explicitly. The adapters do not request credentials, enroll accounts, change calendars or start recording. No Outlook/Google desktop application installation is required for these cloud API paths.

## Required live validation after registration

- Connect Microsoft 365, Outlook.com and a Google test account through the browser; verify restart, token refresh, reconnect and disconnect.
- Enumerate and select calendars. Test recurring meetings, exceptions, time zones/DST, declined/canceled events and multiple calendars with duplicate event IDs.
- Verify exact browser meeting matching and attributed audio availability separately on supported Windows browsers.
- Exercise canceled countdowns, manual recording, dictation conflicts, app shutdown and permission/entitlement loss before enabling automation.
