# Dictionary and snippets preview

## Design question

Can words, corrections, and reusable text use one compact list/editor pattern without making their different roles ambiguous?

This iteration reuses the approved Inter typography, blue glyphs, button styles, switches, and footer breadcrumbs. Three tabs separate Words, Corrections, and Snippets; Quick Launch provides Dictionary and Snippets entry points. The heading and action footer stay fixed while the body scrolls.

## Run

Use the explicitly authorized isolated WinUI build, not the production WPF launcher:

```powershell
dotnet build experiments/winui-quick-launch-prototype/TypeWhisper.WinUIPrototype.csproj -c Debug -r win-x64
```

Launch its `bin/Debug/net10.0-windows10.0.26100.0/win-x64/TypeWhisper.WinUIPrototype.exe` with `--dictionary` or `--snippets`. Close the running prototype before building or changing startup flags. Do not launch the installed production app.

## Scope

- Eight fictional sample entries; all changes live only in this control's session.
- Search keys, replacement text, and tags. Create, edit, disable, and delete entries.
- Words store preferred spellings; corrections store original/replacement pairs; snippets store spoken trigger, multiline text, tags, capitalization matching, and enabled state.
- Blank values, duplicate keys, invalid single-line triggers, no-op corrections, and length limits are validated.
- Unsaved navigation asks whether to keep editing or discard. Delete has a separate confirmation. Escape dismisses a pending confirmation before going back. Backspace navigates only outside text fields.
- No real transcription, vocabulary boosting, snippet expansion, clipboard access, synchronization, import/export, training, or production persistence. Placeholder tokens remain literal text. This is not a parity claim for the existing WPF implementation.

## Files

- `PrototypeLexicon.cs`: immutable entry records and session store, independent of WinUI.
- `PrototypeLexiconView.cs`: shared list/editor UI and draft navigation.
- `App.xaml`: multiline editor template.
- `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml.cs`: isolated view host, commands, key routing, and startup flags.
- `../winui-lexicon-prototype-check`: standalone model checks.

## Verification

```powershell
dotnet run --project experiments/winui-lexicon-prototype-check/TypeWhisper.LexiconPrototypeCheck.csproj
```

28 checks passed: create/update, whitespace normalization, duplicate/case collisions, independent kind namespaces, empty and invalid values, limits, search, multiline preservation, enabled state, exact-ID deletion, and sample isolation.

Build succeeded with zero warnings/errors. Native UI walkthrough at 780 × 520 verified:

- Words, Corrections, and Snippets tabs and empty search state.
- New word with German umlauts; Escape protects its unsaved draft; Keep editing preserves it; Save returns to the updated list.
- Snippet editor opens with its trigger and tags; multiline sample content is visible; Cancel returns to the list.
- Fixed footer, focused input border, and extra clearance for the body scrollbar.

The walkthrough caught initial multiline truncation: `Text` must be assigned after `AcceptsReturn`. Corrected and visually rechecked after rebuilding. The runtime error log remained unchanged from its pre-task baseline.

Still unverified through UI: destructive confirmation completion (model removal is tested), very long content scrolling, high-contrast/light themes, and multi-monitor/200% scaling. No production integration is included.
