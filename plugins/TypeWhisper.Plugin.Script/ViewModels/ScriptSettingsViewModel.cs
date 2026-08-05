using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace TypeWhisper.Plugin.Script;

internal enum ConfirmationChoice
{
    Primary,
    Secondary,
    Cancel
}

internal interface IScriptEditorHost
{
    Guid? ShowEditor(ScriptEntry? script);
}

internal interface IScriptConfirmationService
{
    ConfirmationChoice ConfirmUnsavedChanges(string scriptName);
    bool ConfirmRemove(string scriptName);
}

internal sealed class ScriptListItemViewModel : ObservableObject
{
    private readonly ScriptSettingsViewModel _owner;
    private ScriptEntry _entry;
    private bool _isDropTarget;
    private bool _dropAfter;

    internal ScriptListItemViewModel(ScriptSettingsViewModel owner, ScriptEntry entry)
    {
        _owner = owner;
        _entry = entry;
    }

    internal ScriptEntry Entry => _entry;
    public Guid Id => _entry.Id;
    public string Name => _entry.Name;
    public string Shell => _entry.Shell;
    public string TimeoutText => $"{_entry.TimeoutSeconds}s";
    public string CommandPreview => _entry.Command.ReplaceLineEndings(" ");
    public bool IsDropTarget => _isDropTarget;
    public bool ShowDropBefore => _isDropTarget && !_dropAfter;
    public bool ShowDropAfter => _isDropTarget && _dropAfter;

    public bool IsEnabled
    {
        get => _entry.IsEnabled;
        set => _owner.SetEnabled(this, value);
    }

    internal void Update(ScriptEntry entry)
    {
        _entry = entry;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Shell));
        OnPropertyChanged(nameof(TimeoutText));
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(IsEnabled));
    }

    internal void SetDropIndicator(bool isDropTarget, bool dropAfter)
    {
        if (_isDropTarget == isDropTarget && _dropAfter == dropAfter)
            return;
        _isDropTarget = isDropTarget;
        _dropAfter = dropAfter;
        OnPropertyChanged(nameof(IsDropTarget));
        OnPropertyChanged(nameof(ShowDropBefore));
        OnPropertyChanged(nameof(ShowDropAfter));
    }
}

internal sealed class ScriptSettingsViewModel : ObservableObject
{
    private readonly ScriptService _service;
    private readonly IScriptEditorHost _editorHost;
    private readonly IScriptConfirmationService _confirmations;
    private ScriptListItemViewModel? _selectedItem;
    private string _operationError = "";

    internal ScriptSettingsViewModel(
        ScriptService service,
        IScriptEditorHost editorHost,
        IScriptConfirmationService confirmations)
    {
        _service = service;
        _editorHost = editorHost;
        _confirmations = confirmations;

        AddCommand = new RelayCommand(Add, () => !IsReadOnly);
        EditCommand = new RelayCommand(EditSelected, () => SelectedItem is not null && !IsReadOnly);
        EditItemCommand = new RelayCommand<ScriptListItemViewModel>(Edit, _ => !IsReadOnly);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedItem is not null && !IsReadOnly);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        ReloadItems();
    }

    public ObservableCollection<ScriptListItemViewModel> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public string EditLabel => L("Settings.Edit");
    public bool IsReadOnly => _service.IsReadOnly;
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadErrorMessage);
    public string LoadErrorMessage => IsReadOnly ? L("Settings.LoadError", _service.LoadError ?? "") : "";
    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);

    public string OperationError
    {
        get => _operationError;
        private set
        {
            if (SetProperty(ref _operationError, value))
                OnPropertyChanged(nameof(HasOperationError));
        }
    }

    public ScriptListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
                RaiseCommandStates();
        }
    }

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    internal string L(string key, params object[] args)
    {
        var localization = _service.Localization;
        return localization is null
            ? key
            : localization.GetString(key, args);
    }

    internal void EditSelected()
    {
        if (SelectedItem is not null)
            Edit(SelectedItem);
    }

    internal void MoveItem(ScriptListItemViewModel item, int targetIndex)
    {
        var currentIndex = Items.IndexOf(item);
        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, Items.Count - 1));
        if (currentIndex < 0 || currentIndex == targetIndex || IsReadOnly)
            return;

        try
        {
            _service.MoveTo(item.Id, targetIndex);
            ReloadItems(item.Id);
            OperationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationError = L("Settings.SaveFailed", ex.Message);
        }
    }

    internal void SetEnabled(ScriptListItemViewModel item, bool isEnabled)
    {
        if (item.Entry.IsEnabled == isEnabled)
            return;
        if (IsReadOnly)
        {
            item.Update(item.Entry);
            return;
        }

        var updated = item.Entry with { IsEnabled = isEnabled };
        try
        {
            _service.UpdateScript(updated);
            item.Update(updated);
            OperationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationError = L("Settings.SaveFailed", ex.Message);
            item.Update(item.Entry);
        }
    }

    private void Add()
    {
        if (IsReadOnly)
            return;

        var savedId = _editorHost.ShowEditor(null);
        if (savedId is not null)
            ReloadItems(savedId);
    }

    private void Edit(ScriptListItemViewModel item)
    {
        if (IsReadOnly)
            return;

        SelectedItem = item;
        var savedId = _editorHost.ShowEditor(item.Entry);
        if (savedId is not null)
            ReloadItems(savedId);
    }

    private void RemoveSelected()
    {
        if (SelectedItem is not { } selected
            || IsReadOnly
            || !_confirmations.ConfirmRemove(selected.Name))
        {
            return;
        }

        try
        {
            var oldIndex = Items.IndexOf(selected);
            _service.RemoveScript(selected.Id);
            ReloadItems();
            if (Items.Count > 0)
                SelectedItem = Items[Math.Min(oldIndex, Items.Count - 1)];
            OperationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationError = L("Settings.SaveFailed", ex.Message);
        }
    }

    private void MoveSelected(int offset)
    {
        if (SelectedItem is not { } selected || !CanMove(offset))
            return;

        try
        {
            if (offset < 0)
                _service.MoveUp(selected.Id);
            else
                _service.MoveDown(selected.Id);
            ReloadItems(selected.Id);
            OperationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationError = L("Settings.SaveFailed", ex.Message);
        }
    }

    private bool CanMove(int offset)
    {
        if (SelectedItem is null || IsReadOnly)
            return false;
        var index = Items.IndexOf(SelectedItem);
        var target = index + offset;
        return target >= 0 && target < Items.Count;
    }

    private void ReloadItems(Guid? selectedId = null)
    {
        selectedId ??= SelectedItem?.Id;
        var byId = new Dictionary<Guid, Queue<ScriptListItemViewModel>>();
        foreach (var item in Items)
        {
            if (!byId.TryGetValue(item.Id, out var matches))
            {
                matches = new Queue<ScriptListItemViewModel>();
                byId.Add(item.Id, matches);
            }
            matches.Enqueue(item);
        }

        var ordered = new List<ScriptListItemViewModel>();
        foreach (var script in _service.Scripts)
        {
            ScriptListItemViewModel item;
            if (byId.TryGetValue(script.Id, out var matches) && matches.TryDequeue(out var existing))
            {
                item = existing;
                item.Update(script);
            }
            else
                item = new ScriptListItemViewModel(this, script);
            ordered.Add(item);
        }

        Items.Clear();
        foreach (var item in ordered)
            Items.Add(item);

        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasLoadError));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { AddCommand, EditCommand, RemoveCommand, MoveUpCommand, MoveDownCommand })
        {
            if (command is RelayCommand relay)
                relay.RaiseCanExecuteChanged();
        }
    }
}
