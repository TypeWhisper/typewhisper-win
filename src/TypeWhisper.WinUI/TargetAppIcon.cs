using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace TypeWhisper.WinUI;

internal static class TargetAppIcon
{
    // Ask the Windows shell for the executable's icon; never launch the file.
    // Failures (closed/protected processes or missing shell icons) are cosmetic.
    internal static async Task<BitmapImage?> LoadAsync(uint processId)
    {
        if (processId == 0) return null;
        try
        {
            var path = await Task.Run(() =>
            {
                using var process = Process.GetProcessById(checked((int)processId));
                return process.MainModule?.FileName;
            });
            if (string.IsNullOrEmpty(path)) return null;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return null;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
