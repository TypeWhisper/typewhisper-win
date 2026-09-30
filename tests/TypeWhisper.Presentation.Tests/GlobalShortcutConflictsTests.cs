using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Pins which global shortcut blocks which, and with which message; values are canonical chords.
public sealed class GlobalShortcutConflictsTests
{
    private const string Dictation = "MainDictationHotkeys", Cancel = "CancelProcessingHotkeys";
    private const string PushToTalk = "PushToTalkHotkey", Toggle = "ToggleOnlyHotkeys", Hold = "HoldOnlyHotkeys";
    private const string Recorder = "RecorderToggleHotkeys", Palette = "WorkflowPaletteHotkeys", Recent = "RecentTranscriptionsHotkeys";
    private const string CopyLast = "CopyLastTranscriptionHotkeys", PasteLast = "PasteLastTranscriptionHotkeys", ReadLast = "ReadLastTranscriptionHotkeys";
    private const string Workflows = "Workflows";
    // Check order: the first overlapping shortcut decides the message.
    private static readonly string[] Order = [Dictation, Cancel, PushToTalk, Toggle, Hold, Recorder, Palette, Recent, ReadLast, CopyLast, PasteLast, Workflows];

    private static string? Resolve(string candidate, string value, IReadOnlyDictionary<string, string> registered) =>
        GlobalShortcuts.FindConflict(candidate, value, key => registered.GetValueOrDefault(key, ""));

    private static bool ModifierOnly(string key) => key is Dictation or PushToTalk or Toggle or Hold;

    private static string Expected(string candidate, string other) => other switch
    {
        Dictation => candidate is PushToTalk or Toggle or Hold ? "Already used by Main dictation. Choose a different combination."
            : "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.",
        Cancel => candidate == Dictation ? "This dictation shortcut overlaps Cancel processing. Change the cancel shortcut first."
            : "Already used by Cancel processing.",
        PushToTalk or Toggle or Hold => "Already used by another recording shortcut. Choose a different combination.",
        Recorder => "Already used by Recorder. Change that shortcut first.",
        Palette => "Already used by Workflow palette. Change that shortcut first.",
        Recent => "Already used by Recent transcriptions. Change that shortcut first.",
        CopyLast => "Already used by Copy last transcription. Change that shortcut first.",
        PasteLast => "Already used by Paste last transcription. Change that shortcut first.",
        ReadLast => "Already used by Read last transcription. Change that shortcut first.",
        Workflows => "Already used by a workflow. Change its shortcut in Workflows first.",
        _ => throw new ArgumentOutOfRangeException(nameof(other))
    };

    public static TheoryData<string, string, string, string, string?> Pairs()
    {
        var data = new TheoryData<string, string, string, string, string?>();
        foreach (var candidate in Order)
        foreach (var other in Order.Where(other => other != candidate))
        {
            var message = Expected(candidate, other);
            data.Add(candidate, "CTRL+ALT+K", other, "CTRL+ALT+K", message);
            data.Add(candidate, "CTRL+ALT+J,CTRL+ALT+K", other, "CTRL+ALT+L,CTRL+ALT+K", message);
            data.Add(candidate, "CTRL+ALT+K", other, "CTRL+ALT+J", null);
            data.Add(candidate, "CTRL+ALT+K", other, "CTRL+SHIFT", null);
            // A modifier-only chord blocks every chord that contains its modifiers.
            data.Add(candidate, "CTRL+ALT+K", other, "CTRL+ALT", ModifierOnly(other) ? message : null);
            if (!ModifierOnly(candidate)) continue;
            data.Add(candidate, "CTRL+ALT", other, "CTRL+ALT+K", message);
            data.Add(candidate, "CTRL+ALT", other, "CTRL+SHIFT+K", null);
            data.Add(candidate, "CTRL+ALT+SHIFT", other, "CTRL+ALT", ModifierOnly(other) ? message : null);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryPairReportsItsOverlapWithTheOwnersMessage(string candidate, string value, string other, string registered, string? expected)
        => Assert.Equal(expected, Resolve(candidate, value, new Dictionary<string, string> { [other] = registered }));

    [Theory]
    [MemberData(nameof(Candidates))]
    public void AShortcutNeverConflictsWithItself(string candidate)
        => Assert.Null(Resolve(candidate, "CTRL+ALT+K", new Dictionary<string, string> { [candidate] = "CTRL+ALT+K" }));

    public static TheoryData<string> Candidates() => new(Order);

    [Fact]
    public void UnassignedShortcutsNeverConflict()
    {
        foreach (var candidate in Order)
        {
            Assert.Null(Resolve(candidate, "CTRL+ALT+K", Order.ToDictionary(key => key, _ => "")));
            Assert.Null(Resolve(candidate, "", Order.ToDictionary(key => key, _ => "CTRL+ALT+K")));
        }
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public void FirstOverlapInCheckOrderDecidesTheMessage(string candidate)
    {
        var registered = Order.Where(key => key != candidate).ToDictionary(key => key, _ => "CTRL+ALT+K");
        foreach (var owner in Order.Where(key => key != candidate))
        {
            Assert.Equal(Expected(candidate, owner), Resolve(candidate, "CTRL+ALT+K", registered));
            registered.Remove(owner);
        }
        Assert.Null(Resolve(candidate, "CTRL+ALT+K", registered));
    }

    [Fact]
    public void TheListNamesEveryShortcutOnceInCheckOrder()
    {
        Assert.Equal(Order, GlobalShortcuts.All.Select(shortcut => shortcut.Key));
        Assert.Equal(Order.Where(ModifierOnly), GlobalShortcuts.All.Where(shortcut => shortcut.AllowsModifierOnly).Select(shortcut => shortcut.Key));
    }

    // The shortcut editor reports chords such as "Ctrl+Shift"; MainWindow compares their canonical form.
    [Fact]
    public void EditorChordsAreComparedInCanonicalForm()
    {
        var registered = new Dictionary<string, string> { [PushToTalk] = "CTRL+SHIFT+R" };
        Assert.Equal(Expected(Dictation, PushToTalk), Resolve(Dictation, WorkflowShortcutCatalog.Canonical("Ctrl+Shift"), registered));
        Assert.Equal(Expected(Cancel, PushToTalk), Resolve(Cancel, WorkflowShortcutCatalog.Canonical("control+shift+r"), registered));
    }

    [Theory]
    [InlineData("CTRL+SHIFT", true, "CTRL+SHIFT+R", false, true)]
    [InlineData("CTRL+SHIFT+R", false, "CTRL+SHIFT", true, true)]
    [InlineData("CTRL+SHIFT", true, "CTRL+SHIFT", true, true)]
    [InlineData("CTRL+SHIFT+R", true, "CTRL+SHIFT+T", true, false)]
    [InlineData("CTRL+SHIFT+R", false, "CTRL+SHIFT", false, false)]
    [InlineData("", true, "CTRL+SHIFT", true, false)]
    public void OverlapChecksModifierOnlyChordsInEitherDirection(string first, bool firstModifiers, string second, bool secondModifiers, bool expected)
        => Assert.Equal(expected, GlobalShortcuts.Overlap(first, firstModifiers, second, secondModifiers));
}
