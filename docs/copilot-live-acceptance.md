# GitHub Copilot live acceptance — 2026-09-14

Windows x64, plugin 1.1.0, official GitHub.Copilot.SDK 1.0.13, bundled CLI runtime 1.0.83, model auto. Existing account authentication; no plan upgrade. All inputs below are synthetic. These requests used the real portable package loader, profile save capability and ILlmProviderPlugin.ProcessAsync. They did not capture microphone audio or paste output into another app.

Six real requests completed without API errors. Manual assessment: translation, email drafting and literal preservation met the requested constraints. Dictation cleanup retained the leading filler “Also”, so its strict filler-removal criterion was only partially met. The two-request isolation probe returned the marker only in its own request, then NO_PREVIOUS_MARKER in a fresh request. This is an observed probe, not a proof of all isolation properties.

## German dictation cleanup

Elapsed: 8.72 seconds (including runtime/session setup and cleanup).

Instruction:

Clean up this German dictation: remove filler words and repetitions, add punctuation and capitalization, preserve every factual detail. Return only the corrected text.

Input:

```text
also äh wir treffen uns morgen um halb zehn im büro bitte bring die unterlagen für projekt atlas mit und und sag lea bescheid
```

Output:

```text
Also, wir treffen uns morgen um halb zehn im Büro. Bitte bring die Unterlagen für Projekt Atlas mit, und sag Lea Bescheid.
```

## German to English translation

Elapsed: 6.2 seconds (including runtime/session setup and cleanup).

Instruction:

Translate this German text into natural English. Preserve dates, times, quantities, names and the conditional meaning. Return only the translation.

Input:

```text
Bitte verschiebe unseren Termin vom 17. September um 14:30 Uhr auf den 18. September um 09:00 Uhr. Die 3 Prototypen sollen erst nach Leas Freigabe verschickt werden.
```

Output:

```text
Please move our appointment from September 17 at 2:30 p.m. to September 18 at 9:00 a.m. The 3 prototypes should only be shipped after Lea’s approval.
```

## Notes to email

Elapsed: 5.25 seconds (including runtime/session setup and cleanup).

Instruction:

Write a short, friendly German email from these notes. Preserve the provided facts, invent no facts, promises or names. Return only a subject and the email body. Do not add a sender name.

Input:

```text
Empfängerin: Lea. Danke für Feedback zum Prototyp. Zwei offene Punkte: Kontrast im dunklen Modus; gemeinsame Speichern-Schaltfläche. Beides prüfen wir am Freitag. Bitte Screenshot des Fehlers schicken. Noch keinen Liefertermin zusagen.
```

Output:

```text
Betreff: Offene Punkte zum Prototyp

Hallo Lea,

danke für dein Feedback zum Prototyp. Wir prüfen am Freitag den Kontrast im dunklen Modus und die gemeinsame Speichern-Schaltfläche.

Bitte schick uns noch einen Screenshot des Fehlers. Einen Liefertermin kann ich derzeit noch nicht zusagen.

Viele Grüße
```

## Technical names and literals

Elapsed: 6.43 seconds (including runtime/session setup and cleanup).

Instruction:

Correct only German grammar, capitalization and punctuation. Preserve every technical literal exactly, including case and punctuation. Return only the corrected text.

Input:

```text
bitte prüfe TypeWhisper.WinUI.exe mit dem Modell auto und setze TYPEWHISPER_WINUI_TEST_PROFILE auf copilot-smoke der endpoint ist https://example.invalid/v1 und die datei heißt C:\Temp\test.json version 1.1.0 bleibt unverändert
```

Output:

```text
Bitte prüfe TypeWhisper.WinUI.exe mit dem Modell auto und setze TYPEWHISPER_WINUI_TEST_PROFILE auf copilot-smoke. Der Endpoint ist https://example.invalid/v1, und die Datei heißt C:\Temp\test.json. Version 1.1.0 bleibt unverändert.
```

## Isolation seed

Elapsed: 3.89 seconds (including runtime/session setup and cleanup).

Instruction:

Reply with exactly the marker supplied in the user message, with no extra text.

Input:

```text
TW-ISOLATION-5827
```

Output:

```text
TW-ISOLATION-5827
```

## Isolation follow-up

Elapsed: 3.52 seconds (including runtime/session setup and cleanup).

Instruction:

If this conversation contains an earlier user-supplied marker, return it. Otherwise return exactly NO_PREVIOUS_MARKER. Do not invent or guess a marker.

Input:

```text
What marker did I supply in the previous request?
```

Output:

```text
NO_PREVIOUS_MARKER
```

## Account profile acceptance

The account-bound implementation passed another live translation using `auto` and the existing Copilot CLI OAuth account. A separate check used the real portable package store and runtime registry to save two profiles for that same account, restart the registry, and restore both provider roles. With the primary profile selected in settings, a request through the secondary profile's stable provider identity returned exactly `SECONDARY_PROFILE_OK`.

The functional assertions passed. The helper subsequently exited with a cleanup error because its temporary package DLLs were still loaded. A later attempt to delete that specific temporary directory was blocked by automatic approval review; the directory remains. This does not represent a fully successful helper exit.

All 34 automated tests passed in Release. Two distinct account identities are covered by provider tests and real-SDK loopback tests, including account-specific catalogs, session identity verification, persistence, failed saves and refusal to fall back to another account. A live test with two different signed-in GitHub accounts remains outstanding.

The native settings page displayed the account selector, `Auto`, the add-profile button and one shared **Save profile** footer. A concurrent task was operating the same development app and publishing another checkout into its shared output, so this final check did not exercise a native save click or revalidate this checkout's host branding. The earlier icon check preceded that shared-output conflict. These checks do not cover microphone capture or output insertion.

During PR validation, rebuilding and launching this checkout again confirmed the Copilot icon visibly in the native settings navigation alongside the saved account and `Auto`. Review corrections increased the plugin suite to 35 passing tests, added proxy environment preservation and made package-test cleanup failures fatal after bounded retries. A package rebuild also excluded a planted stale staging file and passed checksum verification. These later checks do not change the limitations of the earlier live helper run.

A second review pass added cancellation and startup regressions: canceling a draft refresh preserves every connected profile using that account, and all saved accounts share one 30-second activation discovery deadline. All 38 plugin tests passed on Windows; explicit caller cancellation still propagates.

Further review regressions verify that a model rejected by live discovery is removed from saved and draft catalogs without disabling other models, and that cancellation immediately after legacy discovery prevents a settings commit. All 41 plugin tests passed on Windows.

The final account-state regression pass verifies late account-discovery cancellation, sign-out invalidation of all matching drafts, and default-provider routing for legacy refresh/model/disconnect operations while a secondary editor is selected. All 44 plugin tests passed on Windows.

The SDK loopback suite additionally covers deferred and mismatched model switches after successful discovery. Both invalidate the requested model and clean up the session without sending user text. All 46 plugin tests passed on Windows.
