using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class OriginalFieldPreferencesTests
{
    [Fact]
    public void OriginalFieldChoiceSurvivesRestartAndIsOffForExistingProfiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"AutoPaste\":true,\"SaveToHistory\":true}");
            var store = new DictationOutputPreferencesStore(path);
            Assert.False(store.Current.LockPasteToFocusedField);
            Assert.Null(store.Save(store.Current with { LockPasteToFocusedField = true }));
            Assert.True(new DictationOutputPreferencesStore(path).Current.LockPasteToFocusedField);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChangingPreferencesNeverDropsTheOriginalFieldRequirementDuringDelivery()
    {
        var locked = new DictationOutputPreferences { LockPasteToFocusedField = true };
        var unlocked = new DictationOutputPreferences();
        Assert.True(locked.RestrictedBy(unlocked).LockPasteToFocusedField);
        Assert.True(unlocked.RestrictedBy(locked).LockPasteToFocusedField);
        Assert.False(locked.RestrictedBy(locked with { AutoPaste = false }).AutoPaste);
    }
}
