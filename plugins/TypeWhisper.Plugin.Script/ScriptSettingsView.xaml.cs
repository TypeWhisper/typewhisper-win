using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Script;

/// <summary>Hosts the Script Runner settings view model.</summary>
public partial class ScriptSettingsView : UserControl
{
    private readonly ScriptSettingsViewModel _viewModel;
    private Point _dragStart;
    private ScriptListItemViewModel? _draggedItem;
    private ScriptListItemViewModel? _dropTarget;
    private bool _dropAfter;

    /// <summary>Initializes the settings view.</summary>
    public ScriptSettingsView(ScriptPlugin plugin)
        : this(plugin.Service ?? throw new InvalidOperationException("The Script Runner plugin is not active."))
    {
    }

    /// <summary>Initializes the settings view.</summary>
    public ScriptSettingsView(ScriptService service)
    {
        InitializeComponent();
        var dialogs = new WindowConfirmationService(() => Window.GetWindow(this), service.Localization);
        var editorHost = new WindowScriptEditorHost(this, service);
        _viewModel = new ScriptSettingsViewModel(service, editorHost, dialogs);
        DataContext = _viewModel;
        Localize();
    }

    internal ScriptSettingsViewModel ViewModel => _viewModel;

    private void OnScriptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScriptList.SelectedItem is not null)
            _viewModel.EditSelected();
    }

    private void OnScriptListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ScriptList);
        _draggedItem = FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is null
            ? FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ScriptListItemViewModel
            : null;
    }

    private void OnScriptListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null)
            return;

        var current = e.GetPosition(ScriptList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _draggedItem;
        _draggedItem = null;
        DragDrop.DoDragDrop(ScriptList, item, DragDropEffects.Move);
        ClearDropIndicator();
    }

    private void OnScriptListDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ScriptListItemViewModel)))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var target = targetContainer?.DataContext as ScriptListItemViewModel;
        var dropAfter = targetContainer is not null
            && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
        if (target is null && _viewModel.Items.Count > 0)
        {
            target = _viewModel.Items[^1];
            dropAfter = true;
        }
        SetDropIndicator(target, dropAfter);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnScriptListDragLeave(object sender, DragEventArgs e)
    {
        if (!ScriptList.IsMouseOver)
            ClearDropIndicator();
    }

    private void OnScriptListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ScriptListItemViewModel)) is not ScriptListItemViewModel source)
            return;

        var sourceIndex = _viewModel.Items.IndexOf(source);
        var insertionIndex = _viewModel.Items.Count;
        if (_dropTarget is not null)
        {
            insertionIndex = _viewModel.Items.IndexOf(_dropTarget);
            if (_dropAfter)
                insertionIndex++;
        }

        ClearDropIndicator();
        if (insertionIndex > sourceIndex)
            insertionIndex--;
        _viewModel.MoveItem(source, insertionIndex);
        e.Handled = true;
    }

    private void SetDropIndicator(ScriptListItemViewModel? target, bool dropAfter)
    {
        if (ReferenceEquals(_dropTarget, target) && _dropAfter == dropAfter)
            return;
        ClearDropIndicator();
        _dropTarget = target;
        _dropAfter = dropAfter;
        _dropTarget?.SetDropIndicator(true, dropAfter);
    }

    private void ClearDropIndicator()
    {
        _dropTarget?.SetDropIndicator(false, false);
        _dropTarget = null;
        _dropAfter = false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Localize()
    {
        PageTitle.Text = L("Settings.ScriptsTitle");
        PageSubtitle.Text = L("Settings.ScriptsSubtitle");
        AddButton.Content = L("Settings.Add");
        EmptyTitle.Text = L("Settings.EmptyTitle");
        EmptyHint.Text = L("Settings.EmptyHint");
        EmptyAddButton.Content = L("Settings.Add");
        EditButton.Content = L("Settings.Edit");
        RemoveButton.Content = L("Settings.Remove");
        MoveUpButton.Content = L("Settings.MoveUp");
        MoveDownButton.Content = L("Settings.MoveDown");
    }

    private string L(string key) => _viewModel.L(key);

    private sealed class WindowScriptEditorHost(ScriptSettingsView view, ScriptService service) : IScriptEditorHost
    {
        public Guid? ShowEditor(ScriptEntry? script)
        {
            var editor = new ScriptEditorWindow(service, script);
            var owner = Window.GetWindow(view);
            if (owner is not null)
                editor.Owner = owner;
            editor.ShowDialog();
            return editor.SavedScript?.Id;
        }
    }
}

internal sealed class WindowConfirmationService(
    Func<Window?> owner,
    IPluginLocalization? localization) : IScriptConfirmationService
{
    public ConfirmationChoice ConfirmUnsavedChanges(string scriptName)
    {
        var dialog = ScriptConfirmationWindow.CreateUnsaved(
            Get("Settings.UnsavedTitle"),
            Get("Settings.UnsavedMessage", scriptName),
            Get("Settings.Save"),
            Get("Settings.Discard"),
            Get("Settings.Cancel"));
        SetOwner(dialog);
        dialog.ShowDialog();
        return dialog.Choice;
    }

    public bool ConfirmRemove(string scriptName)
    {
        var dialog = ScriptConfirmationWindow.CreateRemove(
            Get("Settings.RemoveTitle"),
            Get("Settings.RemoveMessage", scriptName),
            Get("Settings.Remove"),
            Get("Settings.Cancel"));
        SetOwner(dialog);
        dialog.ShowDialog();
        return dialog.Choice == ConfirmationChoice.Primary;
    }

    private void SetOwner(Window dialog)
    {
        if (owner() is { IsVisible: true } window)
            dialog.Owner = window;
    }

    private string Get(string key, params object[] args) => localization is null
        ? key
        : localization.GetString(key, args);
}
