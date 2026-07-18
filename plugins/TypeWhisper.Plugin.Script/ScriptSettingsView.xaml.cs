using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TypeWhisper.Plugin.Script;

/// <summary>
/// Provides script settings view behavior.
/// </summary>
public partial class ScriptSettingsView : UserControl
{
    private readonly ScriptPlugin _plugin;
    private ScriptEntry? _editingScript;
    private bool _suppressEditEvents;
    private bool _suppressSelectionEvents;

    /// <summary>
    /// Initializes a new instance of the ScriptSettingsView class.
    /// </summary>
    public ScriptSettingsView(ScriptPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        if (_plugin.Service is { } service)
        {
            ScriptList.ItemsSource = service.Scripts;
            service.Scripts.CollectionChanged += OnScriptsChanged;
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasScripts = _plugin.Service is { Scripts.Count: > 0 };
        EmptyState.Visibility = hasScripts ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnScriptsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnAddScript(object sender, RoutedEventArgs e)
    {
        var script = new ScriptEntry { Name = "New Script" };
        _plugin.Service?.AddScript(script);

        // Select the newly added script
        ScriptList.SelectedItem = script;
    }

    private void OnRemoveScript(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not ScriptEntry selected) return;
        _plugin.Service?.RemoveScript(selected.Id);
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not ScriptEntry selected) return;
        var service = _plugin.Service;
        if (service is null) return;

        var index = service.Scripts.IndexOf(selected);
        service.MoveUp(selected.Id);

        // Keep selection on the moved item
        if (index > 0) ScriptList.SelectedIndex = index - 1;
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not ScriptEntry selected) return;
        var service = _plugin.Service;
        if (service is null) return;

        var index = service.Scripts.IndexOf(selected);
        service.MoveDown(selected.Id);

        // Keep selection on the moved item
        if (index < service.Scripts.Count - 1) ScriptList.SelectedIndex = index + 1;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents) return;

        if (ScriptList.SelectedItem is not ScriptEntry selected)
        {
            _editingScript = null;
            EditPanel.Visibility = Visibility.Collapsed;
            UpdateEditButtons();
            return;
        }

        LoadEditor(selected);
    }

    private void LoadEditor(ScriptEntry script)
    {
        _editingScript = script;
        _suppressEditEvents = true;
        try
        {
            NameBox.Text = script.Name;
            CommandBox.Text = script.Command;
            ShellCombo.SelectedIndex = -1;

            // Select matching shell in ComboBox
            for (var i = 0; i < ShellCombo.Items.Count; i++)
            {
                if (ShellCombo.Items[i] is ComboBoxItem item
                    && item.Content is string shell
                    && shell.Equals(script.Shell, StringComparison.OrdinalIgnoreCase))
                {
                    ShellCombo.SelectedIndex = i;
                    break;
                }
            }

            EditPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _suppressEditEvents = false;
        }

        UpdateEditButtons();
    }

    private void OnEditFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditEvents) return;
        UpdateEditButtons();
    }

    private void OnSaveEdit(object sender, RoutedEventArgs e)
    {
        if (CreateEditedScript() is not { } updated || updated == _editingScript) return;
        UpdateScriptAndKeepSelection(updated);
        UpdateEditButtons();
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e)
    {
        if (_editingScript is { } script) LoadEditor(script);
    }

    private ScriptEntry? CreateEditedScript()
    {
        if (_editingScript is not { } script) return null;

        return script with
        {
            Name = NameBox.Text,
            Command = CommandBox.Text,
            Shell = (ShellCombo.SelectedItem as ComboBoxItem)?.Content as string ?? script.Shell
        };
    }

    private void UpdateEditButtons()
    {
        var hasChanges = CreateEditedScript() is { } edited && edited != _editingScript;
        SaveButton.IsEnabled = hasChanges;
        CancelButton.IsEnabled = hasChanges;
    }

    private void UpdateScriptAndKeepSelection(ScriptEntry updated)
    {
        if (_plugin.Service is not { } service) return;

        _suppressSelectionEvents = true;
        try
        {
            service.UpdateScript(updated);
            ScriptList.SelectedItem = updated;
            _editingScript = updated;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Guid id, IsChecked: var isChecked }) return;
        var existing = _plugin.Service?.Scripts.FirstOrDefault(s => s.Id == id);
        if (existing is null) return;

        var updated = existing with { IsEnabled = isChecked == true };
        if (_editingScript?.Id == id)
        {
            UpdateScriptAndKeepSelection(updated);
            UpdateEditButtons();
        }
        else
        {
            _plugin.Service?.UpdateScript(updated);
        }
    }
}
