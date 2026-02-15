using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class SnippetEditorWindow : Window
{
    private readonly SnippetsViewModel _vm;

    public SnippetEditorWindow(SnippetsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void InsertPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string placeholder })
        {
            var caretIndex = ReplacementBox.CaretIndex;
            ReplacementBox.Text = ReplacementBox.Text.Insert(caretIndex, placeholder);
            ReplacementBox.CaretIndex = caretIndex + placeholder.Length;
            ReplacementBox.Focus();
        }
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
