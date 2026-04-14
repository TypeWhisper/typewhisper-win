using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class PromptsSection : UserControl
{
    public PromptsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (GetViewModel() is { } vm)
        {
            vm.Actions.CollectionChanged += OnActionsChanged;
            vm.RefreshProviders();
            UpdateEmptyState(vm.Actions.Count);
        }
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (GetViewModel() is { } vm)
            UpdateEmptyState(vm.Actions.Count);
    }

    private void UpdateEmptyState(int count)
    {
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionListHost.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: PromptAction action })
        {
            GetViewModel()?.StartEditCommand.Execute(action);
            e.Handled = true;
        }
    }

    private void ContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PromptAction action })
            GetViewModel()?.StartEditCommand.Execute(action);
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PromptAction action })
            GetViewModel()?.DeleteActionCommand.Execute(action);
    }

    private void ContextMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PromptAction action })
            GetViewModel()?.MoveUpCommand.Execute(action);
    }

    private void ContextMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PromptAction action })
            GetViewModel()?.MoveDownCommand.Execute(action);
    }

    private void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PromptAction action })
            GetViewModel()?.ToggleEnabledCommand.Execute(action);
    }

    private PromptsViewModel? GetViewModel()
    {
        return (DataContext as SettingsWindowViewModel)?.Prompts;
    }
}
