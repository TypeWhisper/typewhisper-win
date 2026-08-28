using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WindowsClipboardTransactionTests
{
    private const string CustomFormat = "TypeWhisper.Test.CustomClipboardFormat";
    private const string UnavailableFormat = "TypeWhisper.Test.UnavailableClipboardFormat";
    private static readonly object ClipboardTestLock = new();

    [Fact]
    public void TemporaryText_RestoresCommonAndRegisteredFormatsAndHonorsSequenceGuard()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                var temporaryFile = Path.GetTempFileName();
                var releasedFormats = new List<uint>();
                using var transaction = new WindowsClipboardTransaction(
                    Dispatcher.CurrentDispatcher,
                    (format, _) => releasedFormats.Add(format));
                IClipboardLease? originalClipboard = null;
                try
                {
                    originalClipboard = transaction.BeginTemporaryTextAsync(
                            "__typewhisper-test-backup__",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    var htmlBytes = Encoding.UTF8.GetBytes("<b>previous</b>\0");
                    var rtfBytes = Encoding.ASCII.GetBytes("{\\rtf1 previous}\0");
                    var customBytes = new byte[] { 1, 2, 3, 4 };
                    var formats = SeedNativeClipboard(
                        temporaryFile,
                        htmlBytes,
                        rtfBytes,
                        customBytes);
                    var originalFormatOrder = EnumerateClipboardFormats();
                    releasedFormats.Clear();

                    using (var lease = transaction.BeginTemporaryTextAsync(
                                   "dictated",
                                   CancellationToken.None)
                               .GetAwaiter()
                               .GetResult())
                    {
                        Assert.Equal("dictated", ReadUnicodeText());
                        var exclusionFormat = NativeMethods.RegisterClipboardFormat(
                            WindowsClipboardTransaction.ExcludeClipboardContentFromMonitorProcessing);
                        var temporaryFormats = EnumerateClipboardFormats().ToList();
                        Assert.Contains(exclusionFormat, temporaryFormats);
                        Assert.True(
                            temporaryFormats.IndexOf(exclusionFormat) <
                            temporaryFormats.IndexOf(NativeMethods.CF_UNICODETEXT));

                        var restoreResult = transaction.RestoreAsync(lease, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();

                        Assert.Equal(ClipboardRestoreResult.Restored, restoreResult);
                    }

                    Assert.Equal(originalFormatOrder, releasedFormats);
                    Assert.Equal(originalFormatOrder, EnumerateClipboardFormats());
                    Assert.Equal("previous", Clipboard.GetText());
                    Assert.True(Clipboard.ContainsData(DataFormats.Html));
                    Assert.True(Clipboard.ContainsData(DataFormats.Rtf));
                    Assert.True(Clipboard.ContainsImage());
                    Assert.Equal(temporaryFile, Assert.Single(Clipboard.GetFileDropList().Cast<string>()));
                    AssertGlobalBytesStartWith(htmlBytes, formats.Html);
                    AssertGlobalBytesStartWith(rtfBytes, formats.Rtf);
                    AssertGlobalBytesStartWith(customBytes, formats.Custom);
                    Assert.False(Clipboard.ContainsData(
                        WindowsClipboardTransaction.ExcludeClipboardContentFromMonitorProcessing));

                    using (var guardedLease = transaction.BeginTemporaryTextAsync(
                                   "dictated-again",
                                   CancellationToken.None)
                               .GetAwaiter()
                               .GetResult())
                    {
                        Clipboard.SetText("newer");

                        var guardedResult = transaction.RestoreAsync(
                                guardedLease,
                                CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();

                        Assert.Equal(ClipboardRestoreResult.ClipboardChanged, guardedResult);
                        Assert.Equal("newer", Clipboard.GetText());
                    }

                    transaction.SetPersistentTextAsync("copied", CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    var persistentExclusionFormat = NativeMethods.RegisterClipboardFormat(
                        WindowsClipboardTransaction.ExcludeClipboardContentFromMonitorProcessing);
                    Assert.Equal("copied", ReadUnicodeText());
                    Assert.DoesNotContain(persistentExclusionFormat, EnumerateClipboardFormats());
                }
                finally
                {
                    if (originalClipboard is not null)
                    {
                        transaction.AcceptSequence(
                            originalClipboard,
                            NativeMethods.GetClipboardSequenceNumber());
                        transaction.RestoreAsync(originalClipboard, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                        originalClipboard.Dispose();
                    }

                    File.Delete(temporaryFile);
                }
            });
        }
    }

    [Fact]
    public void TemporaryText_RestoresAnOriginallyEmptyClipboard()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                using var transaction = new WindowsClipboardTransaction(Dispatcher.CurrentDispatcher);
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    EmptyNativeClipboard();
                    Assert.Empty(EnumerateClipboardFormats());

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", ReadUnicodeText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Empty(EnumerateClipboardFormats());
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Fact]
    public void TemporaryText_HandlesEmptyWindowsInformationProtectionMarker()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                using var transaction = new WindowsClipboardTransaction(Dispatcher.CurrentDispatcher);
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    using var enterpriseOwner = SeedEmptyEnterpriseClipboardMarker();
                    var enterpriseFormat = NativeMethods.RegisterClipboardFormat(
                        WindowsClipboardTransaction.EnterpriseDataProtectionId);
                    Assert.Contains(enterpriseFormat, EnumerateClipboardFormats());
                    Assert.Equal(IntPtr.Zero, ReadClipboardHandle(enterpriseFormat));
                    Assert.True(string.IsNullOrEmpty(ReadEnterpriseClipboardId()));

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", ReadUnicodeText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Equal("previous", ReadUnicodeText());
                    Assert.True(string.IsNullOrEmpty(ReadEnterpriseClipboardId()));
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Fact]
    public void OleBitmap_RestoresImageWhenRegisteredBitmapAliasCannotBeMaterialized()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                var releasedFormats = new List<uint>();
                using var transaction = new WindowsClipboardTransaction(
                    Dispatcher.CurrentDispatcher,
                    (format, _) => releasedFormats.Add(format));
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    var bitmapAliasFormat = NativeMethods.RegisterClipboardFormat(
                        "System.Drawing.Bitmap");
                    Assert.NotEqual(0u, bitmapAliasFormat);
                    var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                        1,
                        1,
                        96,
                        96,
                        System.Windows.Media.PixelFormats.Bgra32,
                        null,
                        new byte[] { 0x11, 0x22, 0x33, 0xFF },
                        4);
                    bitmap.Freeze();
                    var seedData = new DataObject();
                    seedData.SetData(DataFormats.UnicodeText, "previous", autoConvert: false);
                    seedData.SetImage(bitmap);
                    Clipboard.SetDataObject(seedData, copy: true);
                    Assert.Contains(bitmapAliasFormat, EnumerateClipboardFormats());
                    Assert.Equal(
                        IntPtr.Zero,
                        ReadClipboardHandleWithError(bitmapAliasFormat).Handle);
                    releasedFormats.Clear();

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", Clipboard.GetText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Equal("previous", Clipboard.GetText());
                    var restoredBitmap = Clipboard.GetImage();
                    Assert.NotNull(restoredBitmap);
                    Assert.Equal(1, restoredBitmap.PixelWidth);
                    Assert.Equal(1, restoredBitmap.PixelHeight);
                    Assert.NotEmpty(releasedFormats);
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Fact]
    public void DelayedDib_RestoresAvailableBitmapRepresentation()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                using var transaction = new WindowsClipboardTransaction(Dispatcher.CurrentDispatcher);
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    using var bitmapOwner = SeedBitmapWithUnavailableDib();
                    Assert.Contains(NativeMethods.CF_BITMAP, EnumerateClipboardFormats());
                    Assert.Contains(NativeMethods.CF_DIB, EnumerateClipboardFormats());
                    Assert.NotEqual(IntPtr.Zero, ReadClipboardHandle(NativeMethods.CF_BITMAP));
                    Assert.Equal(IntPtr.Zero, ReadClipboardHandle(NativeMethods.CF_DIB));

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", Clipboard.GetText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    var restoredBitmap = Clipboard.GetImage();
                    Assert.NotNull(restoredBitmap);
                    Assert.Equal(1, restoredBitmap.PixelWidth);
                    Assert.Equal(1, restoredBitmap.PixelHeight);
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Fact]
    public void UnavailableDib_DoesNotBlockTemporaryTextAndRestoresRemainingFormats()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                using var transaction = new WindowsClipboardTransaction(Dispatcher.CurrentDispatcher);
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    using var bitmapOwner = SeedTextWithUnavailableDib();
                    Assert.Contains(NativeMethods.CF_DIB, EnumerateClipboardFormats());
                    Assert.Equal(IntPtr.Zero, ReadClipboardHandle(NativeMethods.CF_DIB));

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", Clipboard.GetText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Equal("previous", Clipboard.GetText());
                    Assert.DoesNotContain(NativeMethods.CF_DIB, EnumerateClipboardFormats());
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Theory]
    [InlineData("FileContents")]
    [InlineData("FileName")]
    public void UnavailableShellTransferFormat_DoesNotBlockTemporaryTextAndRestoresRemainingFormats(
        string formatName)
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                using var transaction = new WindowsClipboardTransaction(Dispatcher.CurrentDispatcher);
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    using var owner = SeedTextWithUnavailableRegisteredFormat(
                        formatName,
                        out var unavailableFormat);
                    Assert.Contains(unavailableFormat, EnumerateClipboardFormats());
                    Assert.Equal(IntPtr.Zero, ReadClipboardHandle(unavailableFormat));

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal("dictated", Clipboard.GetText());

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Equal("previous", Clipboard.GetText());
                    Assert.DoesNotContain(unavailableFormat, EnumerateClipboardFormats());
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    [Fact]
    public void UnavailableUniqueFormat_ClearsClipboardAndAllowsTemporaryText()
    {
        lock (ClipboardTestLock)
        {
            RunOnStaThread(() =>
            {
                var releasedFormats = new List<uint>();
                using var transaction = new WindowsClipboardTransaction(
                    Dispatcher.CurrentDispatcher,
                    (format, _) => releasedFormats.Add(format));
                var originalClipboard = BackupClipboard(transaction);
                try
                {
                    using var owner = SeedUnavailableUniqueFormat();
                    releasedFormats.Clear();

                    using var lease = transaction.BeginTemporaryTextAsync(
                            "dictated",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal("dictated", Clipboard.GetText());
                    Assert.NotEmpty(releasedFormats);

                    var result = transaction.RestoreAsync(lease, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.Equal(ClipboardRestoreResult.Restored, result);
                    Assert.Empty(EnumerateClipboardFormats());
                }
                finally
                {
                    RestoreClipboardBackup(transaction, originalClipboard);
                }
            });
        }
    }

    private static IClipboardLease BackupClipboard(WindowsClipboardTransaction transaction) =>
        transaction.BeginTemporaryTextAsync(
                "__typewhisper-test-backup__",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static void RestoreClipboardBackup(
        WindowsClipboardTransaction transaction,
        IClipboardLease originalClipboard)
    {
        transaction.AcceptSequence(
            originalClipboard,
            NativeMethods.GetClipboardSequenceNumber());
        transaction.RestoreAsync(originalClipboard, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        originalClipboard.Dispose();
    }

    private static void EmptyNativeClipboard()
    {
        using var owner = new HwndSource(new HwndSourceParameters("TypeWhisper empty clipboard test owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });
        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static HwndSource SeedEmptyEnterpriseClipboardMarker()
    {
        var enterpriseFormat = NativeMethods.RegisterClipboardFormat(
            WindowsClipboardTransaction.EnterpriseDataProtectionId);
        Assert.NotEqual(0u, enterpriseFormat);

        var owner = new HwndSource(new HwndSourceParameters(
            "TypeWhisper enterprise clipboard test owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            SetGlobalData(
                NativeMethods.CF_UNICODETEXT,
                Encoding.Unicode.GetBytes("previous\0"));
            Marshal.SetLastPInvokeError(0);
            Assert.Equal(
                IntPtr.Zero,
                NativeMethods.SetClipboardData(enterpriseFormat, IntPtr.Zero));
            Assert.Equal(0, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return owner;
    }

    private static IntPtr ReadClipboardHandle(uint format)
    {
        var result = ReadClipboardHandleWithError(format);
        Assert.Equal(0, result.Error);
        return result.Handle;
    }

    private static (IntPtr Handle, int Error) ReadClipboardHandleWithError(uint format)
    {
        Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
        try
        {
            Marshal.SetLastPInvokeError(0);
            var handle = NativeMethods.GetClipboardData(format);
            return (handle, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static HwndSource SeedBitmapWithUnavailableDib()
    {
        var owner = new HwndSource(new HwndSourceParameters("TypeWhisper delayed bitmap owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            var bitmap = CreateBitmap(
                1,
                1,
                1,
                32,
                new byte[] { 0x11, 0x22, 0x33, 0xFF });
            Assert.NotEqual(IntPtr.Zero, bitmap);
            if (NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, bitmap) == IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
                throw new ExternalException(
                    "Could not seed the delayed native bitmap clipboard format.",
                    Marshal.GetLastPInvokeError());
            }

            Marshal.SetLastPInvokeError(0);
            Assert.Equal(
                IntPtr.Zero,
                NativeMethods.SetClipboardData(NativeMethods.CF_DIB, IntPtr.Zero));
            Assert.Equal(0, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return owner;
    }

    private static HwndSource SeedTextWithUnavailableDib()
    {
        var owner = new HwndSource(new HwndSourceParameters("TypeWhisper unavailable bitmap owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            SetGlobalData(
                NativeMethods.CF_UNICODETEXT,
                Encoding.Unicode.GetBytes("previous\0"));
            Marshal.SetLastPInvokeError(0);
            Assert.Equal(
                IntPtr.Zero,
                NativeMethods.SetClipboardData(NativeMethods.CF_DIB, IntPtr.Zero));
            Assert.Equal(0, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return owner;
    }

    private static HwndSource SeedTextWithUnavailableRegisteredFormat(
        string formatName,
        out uint unavailableFormat)
    {
        unavailableFormat = NativeMethods.RegisterClipboardFormat(formatName);
        Assert.NotEqual(0u, unavailableFormat);
        var owner = new HwndSource(new HwndSourceParameters(
            $"TypeWhisper unavailable {formatName} owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            SetGlobalData(
                NativeMethods.CF_UNICODETEXT,
                Encoding.Unicode.GetBytes("previous\0"));
            Marshal.SetLastPInvokeError(0);
            Assert.Equal(
                IntPtr.Zero,
                NativeMethods.SetClipboardData(unavailableFormat, IntPtr.Zero));
            Assert.Equal(0, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return owner;
    }

    private static HwndSource SeedUnavailableUniqueFormat()
    {
        var unavailableFormat = NativeMethods.RegisterClipboardFormat(UnavailableFormat);
        Assert.NotEqual(0u, unavailableFormat);
        var owner = new HwndSource(new HwndSourceParameters("TypeWhisper delayed format owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            SetGlobalData(
                NativeMethods.CF_UNICODETEXT,
                Encoding.Unicode.GetBytes("previous\0"));
            Marshal.SetLastPInvokeError(0);
            Assert.Equal(
                IntPtr.Zero,
                NativeMethods.SetClipboardData(unavailableFormat, IntPtr.Zero));
            Assert.Equal(0, Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return owner;
    }

    private static string? ReadEnterpriseClipboardId()
    {
        var result = NativeMethods.EdpGetEnterpriseIdForClipboard(out var enterpriseIdPointer);
        Assert.True(result >= 0);
        if (enterpriseIdPointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(enterpriseIdPointer);
        }
        finally
        {
            Assert.True(NativeMethods.HeapFree(
                NativeMethods.GetProcessHeap(),
                0,
                enterpriseIdPointer));
        }
    }

    private static ClipboardFormats SeedNativeClipboard(
        string filePath,
        byte[] htmlBytes,
        byte[] rtfBytes,
        byte[] customBytes)
    {
        var html = NativeMethods.RegisterClipboardFormat(DataFormats.Html);
        var rtf = NativeMethods.RegisterClipboardFormat(DataFormats.Rtf);
        var custom = NativeMethods.RegisterClipboardFormat(CustomFormat);
        Assert.NotEqual(0u, html);
        Assert.NotEqual(0u, rtf);
        Assert.NotEqual(0u, custom);

        using var owner = new HwndSource(new HwndSourceParameters("TypeWhisper clipboard test owner")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });

        Assert.True(NativeMethods.OpenClipboard(owner.Handle));
        try
        {
            Assert.True(NativeMethods.EmptyClipboard());
            SetGlobalData(
                NativeMethods.CF_UNICODETEXT,
                Encoding.Unicode.GetBytes("previous\0"));
            SetGlobalData(html, htmlBytes);
            SetGlobalData(rtf, rtfBytes);
            SetGlobalData(NativeMethods.CF_HDROP, CreateFileDropBytes(filePath));

            var bitmap = CreateBitmap(
                1,
                1,
                1,
                32,
                new byte[] { 0x11, 0x22, 0x33, 0xFF });
            Assert.NotEqual(IntPtr.Zero, bitmap);
            if (NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, bitmap) == IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
                throw new ExternalException(
                    "Could not seed the native bitmap clipboard format.",
                    Marshal.GetLastPInvokeError());
            }

            SetGlobalData(custom, customBytes);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return new ClipboardFormats(html, rtf, custom);
    }

    private static void SetGlobalData(uint format, byte[] bytes)
    {
        var handle = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            (nuint)bytes.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException();

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            throw new ExternalException("Could not lock test clipboard memory.");
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(format, handle) != IntPtr.Zero)
            return;

        var error = Marshal.GetLastPInvokeError();
        NativeMethods.GlobalFree(handle);
        throw new ExternalException(
            $"Could not seed clipboard format {format}.",
            error);
    }

    private static byte[] CreateFileDropBytes(string filePath)
    {
        const int dropFilesSize = 20;
        var paths = Encoding.Unicode.GetBytes(filePath + "\0\0");
        var result = new byte[dropFilesSize + paths.Length];
        BitConverter.GetBytes(dropFilesSize).CopyTo(result, 0);
        BitConverter.GetBytes(1).CopyTo(result, 16);
        paths.CopyTo(result, dropFilesSize);
        return result;
    }

    private static IReadOnlyList<uint> EnumerateClipboardFormats()
    {
        Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
        try
        {
            var formats = new List<uint>();
            uint current = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                current = NativeMethods.EnumClipboardFormats(current);
                if (current == 0)
                {
                    Assert.Equal(0, Marshal.GetLastPInvokeError());
                    return formats;
                }

                formats.Add(current);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static byte[] ReadGlobalBytes(uint format)
    {
        Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
        try
        {
            var handle = NativeMethods.GetClipboardData(format);
            Assert.NotEqual(IntPtr.Zero, handle);
            var length = checked((int)GlobalSize(handle));
            var pointer = NativeMethods.GlobalLock(handle);
            Assert.NotEqual(IntPtr.Zero, pointer);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void AssertGlobalBytesStartWith(byte[] expected, uint format)
    {
        var actual = ReadGlobalBytes(format);
        Assert.True(actual.Length >= expected.Length);
        Assert.Equal(expected, actual.AsSpan(0, expected.Length).ToArray());
    }

    private static string? ReadUnicodeText()
    {
        Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
        try
        {
            var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            Assert.NotEqual(IntPtr.Zero, handle);
            var pointer = NativeMethods.GlobalLock(handle);
            Assert.NotEqual(IntPtr.Zero, pointer);
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        byte[] bits);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr memoryHandle);

    private sealed record ClipboardFormats(uint Html, uint Rtf, uint Custom);
}
