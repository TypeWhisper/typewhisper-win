namespace TypeWhisper.WinUI;

public sealed partial class LexiconView
{
    private LexiconAppImportDialog? _appImportFlow;

    private async Task ImportFromAppAsync()
    {
        if (_closing || _transferCompletion is { Task.IsCompleted: false } || _trainingTask is { IsCompleted: false }) return;
        var completion = _transferCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _appImportFlow = new(this, message => _notice.Text = message);
        try
        {
            var result = await _appImportFlow.ShowAsync(_kind == LexiconKind.Snippet);
            if (_closing) return;
            _store.ReloadDictionary(); _store.ReloadSnippets();
            Render();
            _notice.Text = result ?? "Import canceled. Nothing was changed.";
        }
        finally { _appImportFlow = null; completion.TrySetResult(); }
    }
}
