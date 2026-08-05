using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace TypeWhisper.Plugin.Script;

internal partial class ScriptEditorWindow : Window
{
    private readonly ScriptEditorViewModel _viewModel;
    private bool _allowClose;

    internal ScriptEditorWindow(ScriptService service, ScriptEntry? script)
    {
        InitializeComponent();
        var confirmations = new WindowConfirmationService(() => this, service.Localization);
        _viewModel = new ScriptEditorViewModel(service, script, confirmations);
        _viewModel.CloseRequested += OnCloseRequested;
        DataContext = _viewModel;
        Localize();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal ScriptEditorViewModel ViewModel => _viewModel;
    internal ScriptEntry? SavedScript => _viewModel.SavedScript;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 32);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 32);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        var desiredLeft = Owner is null
            ? workArea.Left + ((workArea.Width - Width) / 2)
            : Owner.Left + ((Owner.ActualWidth - Width) / 2);
        var desiredTop = Owner is null
            ? workArea.Top + ((workArea.Height - Height) / 2)
            : Owner.Top + ((Owner.ActualHeight - Height) / 2);
        var minimumLeft = workArea.Left + 16;
        var minimumTop = workArea.Top + 16;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - Width - 16);
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - Height - 16);
        Left = Math.Clamp(desiredLeft, minimumLeft, maximumLeft);
        Top = Math.Clamp(desiredTop, minimumTop, maximumTop);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _allowClose = _viewModel.SavedScript is not null;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        if (!_viewModel.CanClose())
        {
            e.Cancel = true;
            return;
        }
        _allowClose = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }

    private void Localize()
    {
        Title = _viewModel.L(_viewModel.IsNew ? "Settings.EditorNewTitle" : "Settings.EditorEditTitle");
        EditorTitle.Text = Title;
        EditorSubtitle.Text = _viewModel.L(_viewModel.IsNew ? "Settings.EditorNewSubtitle" : "Settings.EditorEditSubtitle");
        NameLabel.Text = _viewModel.L("Settings.Name");
        ShellLabel.Text = _viewModel.L("Settings.Shell");
        TimeoutLabel.Text = _viewModel.L("Settings.Timeout");
        CommandLabel.Text = _viewModel.L("Settings.Command");
        InputHint.Text = _viewModel.L("Settings.InputHint");
        ExamplesLabel.Text = _viewModel.L("Settings.Examples");
        ExamplesHint.Text = _viewModel.L("Settings.ExamplesHint");
        TestTitle.Text = _viewModel.L("Settings.TestTitle");
        TestInputLabel.Text = _viewModel.L("Settings.TestInput");
        RunTestButton.Content = _viewModel.L("Settings.RunTest");
        CancelTestButton.Content = _viewModel.L("Settings.CancelTest");
        TestStatusLabel.Text = _viewModel.L("Settings.Status");
        TestExitCodeLabel.Text = _viewModel.L("Settings.ExitCode");
        TestElapsedLabel.Text = _viewModel.L("Settings.Elapsed");
        StandardOutputLabel.Text = _viewModel.L("Settings.StandardOutput");
        StandardErrorLabel.Text = _viewModel.L("Settings.StandardError");
        CancelButton.Content = _viewModel.L("Settings.Cancel");
        SaveButton.Content = _viewModel.L("Settings.Save");
        var closeLabel = _viewModel.L("Settings.Close");
        CloseButton.ToolTip = closeLabel;
        AutomationProperties.SetName(CloseButton, closeLabel);
    }
}
