using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class FileTranscriptionSection : UserControl
{
    private SettingsWindowViewModel? _viewModel;

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

        PresentImporter();
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

    private void PresentImporter()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio/Video|*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.flac;*.wma;*.mp4;*.mkv;*.avi;*.mov;*.webm|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
            _viewModel?.FileTranscription.TranscribeFileCommand.Execute(dialog.FileName);
    }
}
