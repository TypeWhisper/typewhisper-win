using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class PromptEditorWindow : Window
{
    private readonly PromptsViewModel _vm;

    public PromptEditorWindow(PromptsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void IconPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string icon })
            _vm.EditIcon = icon;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveEditorCommand.Execute(null);
        if (!_vm.IsEditorOpen)
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelEditorCommand.Execute(null);
        DialogResult = false;
    }
}
