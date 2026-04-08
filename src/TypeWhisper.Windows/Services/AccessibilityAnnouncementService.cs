using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Posts UI Automation announcements for screen reader users (Narrator, NVDA, JAWS).
/// Uses LiveRegionChanged events via a hidden TextBlock.
/// </summary>
public sealed class AccessibilityAnnouncementService
{
    private TextBlock? _liveRegion;

    /// <summary>
    /// Attaches a hidden TextBlock as live region to the given parent panel.
    /// Call once during window initialization.
    /// </summary>
    public void AttachTo(Panel parent)
    {
        _liveRegion = new TextBlock
        {
            Width = 0,
            Height = 0,
            Visibility = Visibility.Hidden
        };
        AutomationProperties.SetLiveSetting(_liveRegion, AutomationLiveSetting.Assertive);
        parent.Children.Add(_liveRegion);
    }

    public void Announce(string message)
    {
        if (_liveRegion is null || string.IsNullOrWhiteSpace(message)) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // Clear then set to ensure change notification fires even for repeated messages
                _liveRegion.Text = "";
                _liveRegion.Text = message;

                var peer = UIElementAutomationPeer.FromElement(_liveRegion)
                    ?? UIElementAutomationPeer.CreatePeerForElement(_liveRegion);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch
            {
                // No screen reader active or automation unavailable
            }
        });
    }

    public void AnnounceRecordingStarted() => Announce("Recording started");

    public void AnnounceTranscriptionComplete(int wordCount) =>
        Announce($"Transcription complete, {wordCount} words");

    public void AnnounceError(string reason) => Announce($"Error: {reason}");
}
