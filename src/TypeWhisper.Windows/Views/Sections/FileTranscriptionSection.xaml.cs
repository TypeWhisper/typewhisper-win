using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class FileTranscriptionSection : UserControl
{
    private SettingsWindowViewModel? _viewModel;
    private bool _isPresentingImporter;

    public FileTranscriptionSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToViewModel(DataContext as SettingsWindowViewModel);
        TryPresentImporter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachToViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachToViewModel(e.NewValue as SettingsWindowViewModel);
    }

    private void AttachToViewModel(SettingsWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;

        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsWindowViewModel.PendingFileImporterRequestId))
            TryPresentImporter();
    }

    private void TryPresentImporter()
    {
        if (_viewModel?.TryConsumePendingFileImporterRequest() != true)
            return;

        if (_isPresentingImporter)
            return;

        _isPresentingImporter = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                PresentImporter();
            }
            finally
            {
                _isPresentingImporter = false;
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            _viewModel?.FileTranscription.HandleFileDrop(files);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSelectFile(object sender, RoutedEventArgs e)
    {
        PresentImporter();
    }

    private void OnSelectWatchFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select watch folder"
        };

        var current = _viewModel?.FileTranscription.WatchFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            _viewModel?.FileTranscription.SetWatchFolderPath(dialog.FolderName);
    }

    private void OnSelectWatchFolderOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select output folder"
        };

        var current = _viewModel?.FileTranscription.WatchFolderOutputPath
            ?? _viewModel?.FileTranscription.WatchFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            _viewModel?.FileTranscription.SetWatchFolderOutputPath(dialog.FolderName);
    }

    private void PresentImporter()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio/Video|*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.flac;*.wma;*.mp4;*.mkv;*.avi;*.mov;*.webm|All Files|*.*"
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is not null
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        if (accepted == true)
            _viewModel?.FileTranscription.AddFilesCommand.Execute(dialog.FileNames);
    }
}
