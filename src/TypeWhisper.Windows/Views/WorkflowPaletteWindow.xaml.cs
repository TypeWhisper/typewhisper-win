using System.Windows;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsScreen = System.Windows.Forms.Screen;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides workflow palette window behavior.
/// </summary>
public partial class WorkflowPaletteWindow : Window
{
    private readonly WorkflowPaletteViewModel _viewModel;
    private bool _isSelecting;
    private bool _isClosing;

    /// <summary>
    /// Initializes a new instance of the WorkflowPaletteWindow class.
    /// </summary>
    public WorkflowPaletteWindow(WorkflowPaletteViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnActiveScreen();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isSelecting)
            Dispatcher.BeginInvoke(RequestClose);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                WorkflowsList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                WorkflowsList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                SelectAndClose(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                RequestClose();
                e.Handled = true;
                break;
        }
    }

    private void Workflow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkflowPaletteItem item)
        {
            SelectAndClose(item);
            e.Handled = true;
        }
    }

    private void SelectAndClose(WorkflowPaletteItem? item)
    {
        if (item is null)
            return;

        _isSelecting = true;
        RequestClose();
        _viewModel.Select(item);
    }

    /// <summary>
    /// Performs request close.
    /// </summary>
    public void RequestClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Close();
    }

    private void PositionOnActiveScreen()
    {
        var cursor = FormsCursor.Position;
        var workArea = FormsScreen.FromPoint(cursor).WorkingArea;

        var source = PresentationSource.FromVisual(this);
        var dpiToWpfX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var dpiToWpfY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        var left = workArea.Left * dpiToWpfX;
        var top = workArea.Top * dpiToWpfY;
        var width = workArea.Width * dpiToWpfX;
        var height = workArea.Height * dpiToWpfY;

        Left = left + (width - Width) / 2;
        Top = top + (height - Height) / 2 - 40;
    }

    private void CenterOnWorkArea(Rect workArea)
    {
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2 - 40;
    }
}
