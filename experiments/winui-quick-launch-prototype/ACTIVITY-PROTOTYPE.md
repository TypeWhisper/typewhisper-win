# Mac-inspired dashboard and statistics

## Question and direction

Can the approved macOS Home/Statistics hierarchy fit the existing WinUI settings shell without becoming a different dashboard design?

One direction was requested: follow the Mac implementation and Marco's Statistics screenshot closely. Read-only references: `TypeWhisper/Views/HomeSettingsView.swift`, `Views/StatisticsView.swift`, and `ViewModels/StatisticsViewModel.swift` in the Mac checkout.

- Home: activity summary with four icon/value cards, followed by recent transcriptions.
- Statistics: four overview cards, four usage metrics, a wide daily activity chart, top apps/models side by side, and a weekday/hour heatmap.
- Existing Inter typography, blue source-owned icons, dark brushes, custom buttons, and settings sidebar are retained.
- Headings and period controls stay fixed; body content scrolls. Four-column metrics become two columns below 600 content pixels; usage rankings stack below 560.
- Bars remain still and start at the zero baseline. No waveform visualization or animation is used.

## Open

Build the explicitly authorized isolated prototype:

```powershell
dotnet build experiments/winui-quick-launch-prototype/TypeWhisper.WinUIPrototype.csproj -c Debug -r win-x64
```

Close the running prototype before rebuilding. Start its `bin/Debug/net10.0-windows10.0.26100.0/win-x64/TypeWhisper.WinUIPrototype.exe` with `--dashboard` or `--statistics`.

Also available through Quick Launch's Dashboard/Statistics commands and the settings sidebar's Home/Statistics entries. Both pages are included in settings search.

## Interaction and isolation

- Week, Month, and All time filter metrics, chart, rankings, and heatmap together.
- Chart bars expose exact counts on hover, focus, click, and through automation names.
- Activity cards lead to Statistics. Recent entries open read-only sample transcript previews. View all history opens the existing history workspace from Quick Launch; if another workspace is active, it explains that returning to Quick Launch first preserves that workspace.
- Preview empty state switches disposable fixtures off without deleting anything. The empty state links to the existing setup wizard.
- Usage events are separate from history fixtures, following the Mac separation between usage aggregates and history retention. This is session-only preview data, not a sync or production storage contract.
- No microphone, telemetry, personal usage measurement, production writes, or external services. Model/app names are example labels, not installed-provider detection.
- Saved time is explicitly an illustrative estimate: words / 40 WPM minus speaking time, clamped at zero. No trends are claimed, and the sample timeline is fixed rather than current personal activity.

## Changed artifacts

- `PrototypeUsageData.cs`: deterministic fixtures and period aggregation.
- `PrototypeActivityView.cs`: dashboard, statistics, sample detail, empty state, responsive cards, charts.
- `TypeWhisperGlyph.cs`: home, statistics, calendar, speed, trophy, and flame glyphs.
- `PrototypeSettingsWindow.xaml` / `.xaml.cs`: view host, sidebar/search entries, navigation.
- `MainWindow.xaml.cs` / `App.xaml.cs`: Quick Launch commands and startup flags.
- `../winui-usage-prototype-check`: standalone model checks.

## Evidence and limitations

```powershell
dotnet run --project experiments/winui-usage-prototype-check/TypeWhisper.UsagePrototypeCheck.csproj
```

37 checks passed: inclusive preset and custom period boundaries; chart/card agreement; app/model/hour totals; empty metrics; zero-filled chart; current/longest streaks including gaps and yesterday; future exclusion for presets; nonnegative time estimates; leap day; single-day ranges including late events; reversed ranges; and preview range bounds. All-time starts at the first actual fixture event, not an earlier unused date.

WinUI build passed with zero warnings/errors. Native UI inspected at 1040 × 780: metric hierarchy, fixed header, daily chart, lower rankings and heatmap. Initial chart inspection caught vertically centered bars caused by the shared button presenter. A full-height plot cell now anchors every bar to zero; confirmed after rebuild. Marco explicitly requested ordinary bars, not a waveform.

Further UI input stopped when Marco began interacting with the window. Period calculations are tested in the model; period-button interaction, dashboard/detail/empty-state walkthroughs, keyboard traversal, narrow windows, 200% scaling, and light/high-contrast themes still need interactive verification. No production promotion is implied by this prototype.

## Custom range follow-up

Added `PrototypeDateRangePicker.cs` and the scoped `PrototypeRangeFlyoutStyle` in `App.xaml`. The top-right Custom button opens a styled calendar with From/To text fields, month navigation, Cancel, and Apply. Dates can be clicked or entered as DD.MM.YYYY / YYYY-MM-DD. Draft edits do not change statistics before Apply; cancellation/light dismissal retains the previously applied range. Both endpoints are inclusive. The accepted preview range is 1900–2100 with up to 3,660 days per selection. Large charts use at most 90 date buckets rather than thousands of controls; other aggregates retain exact event totals. Empty selected periods show zero metrics and an explicit no-activity message.

Native UI verified opening the calendar, selecting August 10 then September 4, applying, and seeing the range label and filtered metrics/chart. Build has zero warnings/errors; runtime error log unchanged. Direct date typing, invalid-input feedback, cancellation and narrow popup placement still need a UI walkthrough (range validation is covered by the model checks).

## Heatmap tooltip follow-up

Marco clarified that hourly values should appear in a floating tooltip, not an inline values row. The inline row was removed. Heatmap cells now use a compact, theme-aware tooltip containing weekday, hour interval, and transcription count; the hovered/focused cell is outlined without changing layout. The existing small rectangular cell shape is retained. A single tab stop and arrow-key navigation make counts accessible without adding 168 sequential tab stops. Escape dismisses the tooltip.

Changed `PrototypeActivityView.cs` and added `PrototypeHeatmapToolTipStyle` in `App.xaml`. Build passed with zero warnings/errors. Native screenshot confirmed the floating tooltip for Monday 10:00–10:59 with 10 transcriptions, anchored immediately above the cell. Full keyboard traversal is not yet UI-verified.

The activity bars now reuse the same floating tooltip style: date (or the complete bucket date range) plus word count. The inline hover-value row was removed; the no-activity message remains for empty periods. Pointer and keyboard focus open the tooltip; leaving/unloading closes it and Escape dismisses it. Build passed with zero warnings/errors. A native screenshot confirmed the zero-count boundary tooltip for August 7, 2026, showing 0 words without shifting the chart layout.
