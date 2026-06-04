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
    private bool _suppressEditEvents;

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
        if (ScriptList.SelectedItem is not ScriptEntry selected)
        {
            EditPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressEditEvents = true;
        try
        {
            NameBox.Text = selected.Name;
            CommandBox.Text = selected.Command;

            // Select matching shell in ComboBox
            for (var i = 0; i < ShellCombo.Items.Count; i++)
            {
                if (ShellCombo.Items[i] is ComboBoxItem item
                    && item.Content is string shell
                    && shell.Equals(selected.Shell, StringComparison.OrdinalIgnoreCase))
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
    }

    private void OnEditFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditEvents) return;
        if (ScriptList.SelectedItem is not ScriptEntry selected) return;

        var shell = (ShellCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "cmd";

        _plugin.Service?.UpdateScript(selected with
        {
            Name = NameBox.Text,
            Command = CommandBox.Text,
            Shell = shell
        });
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Guid id, IsChecked: var isChecked }) return;
        var existing = _plugin.Service?.Scripts.FirstOrDefault(s => s.Id == id);
        if (existing is null) return;

        _plugin.Service?.UpdateScript(existing with { IsEnabled = isChecked == true });
    }
}
