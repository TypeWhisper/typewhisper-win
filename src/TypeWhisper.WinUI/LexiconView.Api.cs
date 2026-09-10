namespace TypeWhisper.WinUI;

public sealed partial class LexiconView
{
    private LexiconEntry? _apiEditorBaseline;

    internal void RefreshApiData()
    {
        if (_closing) return;
        _store.ReloadDictionary();
        _store.ReloadSnippets();
        if (_draft is null) Render();
        else if (ApiEditorConflict())
            _notice.Text = "This entry changed outside this editor. Your draft is still here; copy any changes you need, then reopen the entry before saving.";
    }

    private bool ApiEditorConflict() => _draft is not null &&
        _store.Entries.FirstOrDefault(entry => entry.Id == _draft.Id) != _apiEditorBaseline;

    private bool CanSaveApiEditor()
    {
        _store.ReloadDictionary();
        _store.ReloadSnippets();
        if (_store.LastError is { } error) { _notice.Text = error; return false; }
        if (!ApiEditorConflict()) return true;
        _notice.Text = "This entry changed outside this editor. Your draft is still here; copy any changes you need, then reopen the entry before saving.";
        return false;
    }
}
