# Dictionary term boundaries

Follow-up to #510, separate from the Meta migration.

The previous host prompt joined dictionary entries with commas. Providers then split that string, turning `Washington, D.C.` into two keywords. The host now emits a versioned JSON term envelope only when a provider opts into `SupportsStructuredDictionaryTerms`. Provider term limits are applied before serialization, so JSON escaping does not consume the vocabulary budget.

Meta, AssemblyAI, ElevenLabs, Gemini, OpenAI, Gladia, Speechmatics and Fireworks decode the envelope before building requests. OpenAI and Fireworks unwrap it back to ordinary text when an endpoint requires a textual prompt. Legacy plugins receive the existing comma-separated prompt; the new SDK capability defaults to false. Newly built provider packages require host contract 1.1.5 and reject older hosts before loading.

Validation covers both WAV routing paths, streaming eligibility and connection, legacy routing, punctuation, quotes, newlines, Unicode, de-duplication, ordering, provider limits and malformed envelopes. Each migrated provider has a focused parser regression test. Full provider suites and the portable SDK/host suite are run locally. No real provider API call or microphone acceptance test is claimed for this follow-up yet.

Local validation: 608 provider tests passed across the eight affected packages, plus 282 portable SDK/host tests. Package compatibility cases were rerun after raising the minimum host contract to 1.1.5.
