# Experiments

Experiments are isolated research artifacts, not application projects or release deliverables.

| Experiment | Purpose | Status |
| --- | --- | --- |
| [Browser microphone extension](browser-microphone-extension/README.md) | Explore a microphone control attached to browser text fields. | Proof of concept; no native messaging host or production integration. |

Run its tests from the repository root:

```powershell
node --test experiments/browser-microphone-extension/tests/field-target.test.cjs
```

The old `winui-quick-launch` prototype and its seven standalone check projects were
removed after the WinUI application moved into `src/TypeWhisper.WinUI`. They only
tested prototype copies and were not referenced by the solution, CI or current
application tests. Their source remains in Git history. Use the current
[test guide](../TESTING_GUIDE.md) for application validation.

New experiments should state their design question, how to run them, what they
establish, and what remains unproven. Keep production code and regression tests in
`src/`, `plugins/` and `tests/`.
