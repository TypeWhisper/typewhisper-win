#if TYPEWHISPER_STORE
using System.Diagnostics;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Receives Store package share-target activations and forwards shared media
/// files to the normal shell transcription inbox.
/// </summary>
internal static class StoreShareTargetService
{
    internal static bool TryQueueSharedFiles(IActivatedEventArgs? activationArgs)
    {
        if (activationArgs is not ShareTargetActivatedEventArgs shareTargetArgs)
            return false;

        var shareOperation = shareTargetArgs.ShareOperation;
        shareOperation.ReportStarted();

        try
        {
            if (!shareOperation.Data.Contains(StandardDataFormats.StorageItems))
            {
                shareOperation.ReportError("TypeWhisper did not receive any files to transcribe.");
                return true;
            }

            var sharedItems = shareOperation.Data.GetStorageItemsAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            shareOperation.ReportDataRetrieved();

            var paths = sharedItems
                .Where(item => item.IsOfType(StorageItemTypes.File))
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
            {
                shareOperation.ReportError(
                    "TypeWhisper can only transcribe files that are available on this device.");
                return true;
            }

            if (!ShellTranscriptionService.Enqueue(paths))
            {
                shareOperation.ReportError("TypeWhisper could not queue the shared files.");
                return true;
            }

            shareOperation.ReportCompleted();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Share target activation failed: {ex}");
            try
            {
                shareOperation.ReportError("TypeWhisper could not receive the shared files.");
            }
            catch (Exception reportException)
            {
                Debug.WriteLine($"Share target error reporting failed: {reportException.Message}");
            }
        }

        return true;
    }
}
#endif
