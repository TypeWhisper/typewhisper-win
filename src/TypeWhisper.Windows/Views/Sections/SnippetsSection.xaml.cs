using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class SnippetsSection : UserControl
{
    public SnippetsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (GetViewModel() is { } vm)
        {
            vm.Snippets.CollectionChanged += OnSnippetsChanged;
            UpdateEmptyState(vm.Snippets.Count);
        }
    }

    private void OnSnippetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (GetViewModel() is { } vm)
            UpdateEmptyState(vm.Snippets.Count);
    }

    private void UpdateEmptyState(int count)
    {
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnippetList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: Snippet snippet })
        {
            GetViewModel()?.EditItemCommand.Execute(snippet);
            e.Handled = true;
        }
    }

    private void ContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Snippet snippet })
            GetViewModel()?.EditItemCommand.Execute(snippet);
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Snippet snippet })
            GetViewModel()?.DeleteItemCommand.Execute(snippet);
    }

    private void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: Snippet snippet })
            GetViewModel()?.ToggleEnabledItemCommand.Execute(snippet);
    }

    private SnippetsViewModel? GetViewModel()
    {
        // DataContext is SettingsWindowViewModel, which has a Snippets property
        return (DataContext as SettingsWindowViewModel)?.Snippets;
    }
}
