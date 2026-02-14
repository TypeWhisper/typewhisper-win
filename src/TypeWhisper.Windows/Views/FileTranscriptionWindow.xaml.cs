using System.Windows;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class FileTranscriptionWindow : Window
{
    private readonly FileTranscriptionViewModel _viewModel;

    public FileTranscriptionWindow(FileTranscriptionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            _viewModel.HandleFileDrop(files);
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
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio/Video|*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.flac;*.wma;*.mp4;*.mkv;*.avi;*.mov;*.webm|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.TranscribeFileCommand.Execute(dialog.FileName);
        }
    }
}
