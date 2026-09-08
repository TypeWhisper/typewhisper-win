using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<DictationRecoveryView> _recoveryViews = [];
    private readonly List<Task> _recoveryViewDrains = [];

    private DictationRecoveryView CreateRecoveryView()
    {
        var view = new DictationRecoveryView(_dictation.Recovery,
            _dictation.RecoveryPreferences, _dictation.SaveRecoveryPreferencesAsync);
        _recoveryViews.Add(view);
        return view;
    }

    private void CloseRecoveryView(DictationRecoveryView? view)
    {
        if (view is null || !_recoveryViews.Remove(view)) return;
        // A closed settings window must not leave an untracked writer or decoder.
        // The controller remains reusable when settings are opened again.
        _recoveryViewDrains.Add(view.ShutdownAsync(shutdownController: false));
    }

    private Task DrainRecoveryViewsAsync()
    {
        foreach (var view in _recoveryViews.ToArray()) CloseRecoveryView(view);
        return Task.WhenAll(_recoveryViewDrains.ToArray());
    }
}
