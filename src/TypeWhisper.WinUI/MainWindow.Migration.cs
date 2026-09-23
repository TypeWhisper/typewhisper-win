using System.Text.Json;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal void ShowMigrationNotice()
    {
        var report = WinUIProfile.DataPath(LegacyWindowsProfileMigration.ReportName);
        var acknowledged = WinUIProfile.DataPath("legacy-migration-notice-shown");
        if (!File.Exists(report) || File.Exists(acknowledged)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(report));
            var notes = document.RootElement.GetProperty("Notes").EnumerateArray().Select(item => item.GetString()).ToArray();
            // One line per note: skipped plugins and keys to re-enter must stay readable.
            ShowActivationNotice(string.Join("\n", notes.Prepend("Your previous TypeWhisper profile was copied.")));
            ActivationNoticeTitle.Text = "TypeWhisper upgraded";
            ShowFromActivation();
            File.WriteAllText(acknowledged, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { ShowActivationNotice("Your previous profile was preserved. The upgrade report could not be displayed."); }
    }
}
