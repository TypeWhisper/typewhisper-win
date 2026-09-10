using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class FileTranscriptionView
{
    private async Task ExportAllAsync()
    {
        if (_picking || _queue.Running || _queue.IsShutdown || XamlRoot is null) return;
        var ready = _queue.Jobs.Where(j => j.Status == FileTranscriptionStatus.Ready && j.Result is not null).ToArray();
        if (ready.Length == 0) return;
        _picking = true;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = "Export completed transcripts as TXT" };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || _queue.IsShutdown) return;
            var export = TranscriptFileExport.ExportBatchAsync(ready, folder.Path, "txt");
            _exportOperation = export;
            var failures = await export;
            _notice.Text = $"{ready.Length - failures.Count} transcripts exported." + (failures.Count > 0 ? " " + string.Join(" ", failures) : " Existing files were preserved.");
        }
        catch (Exception) { _notice.Text = "Export could not finish. Your transcripts remain available."; }
        finally { _picking = false; _exportOperation = Task.CompletedTask; if (!_queue.IsShutdown) Render(); }
    }
}
