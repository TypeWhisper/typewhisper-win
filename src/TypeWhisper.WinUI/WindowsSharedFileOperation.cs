using TypeWhisper.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace TypeWhisper.WinUI;

internal sealed class WindowsSharedFileOperation(ShareOperation operation) : ISharedFileOperation
{
    public void ReportStarted() => operation.ReportStarted();
    public void ReportDataRetrieved() => operation.ReportDataRetrieved();
    public void ReportCompleted() => operation.ReportCompleted();
    public void ReportError(string message) => operation.ReportError(message);
    public async Task<IReadOnlyList<string>> ReadPathsAsync()
    {
        if (!operation.Data.Contains(StandardDataFormats.StorageItems))
            throw new InvalidDataException("The share operation does not contain files.");
        var items = await operation.Data.GetStorageItemsAsync();
        // Empty paths and folders are rejected by the shared activation validator.
        return items.Select(item => item is StorageFile file ? file.Path : string.Empty).ToArray();
    }
}
