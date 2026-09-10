using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using Xunit;
using Xunit.Abstractions;

public sealed class LiveClipboardTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeClipboardRoundTripsWithStrictWinUiPolicy()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("TYPEWHISPER_RUN_LIVE_CLIPBOARD_TESTS"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var isolation = new PrivateClipboardStation();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                output.WriteLine("Private window station active: interactive clipboard is not accessed.");
                Run();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Run()
    {
        using var owner = new HwndSource(new HwndSourceParameters("TypeWhisper isolated clipboard test")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        using var transaction = new WindowsClipboardTransaction(owner.Handle);
        // Strict capture is required BEFORE any test fixture overwrites user data.
        using var original = Wait(transaction.BeginTemporaryTextAsync("TypeWhisper clipboard test", CancellationToken.None));
        var expected = NativeMethods.GetClipboardSequenceNumber();
        Assert.True(transaction.IsCurrent(original));
        try
        {
            void Seed(Action write)
            {
                Assert.True(TryOpen(owner.Handle), "Clipboard busy; aborting.");
                try
                {
                    Assert.Equal(expected, NativeMethods.GetClipboardSequenceNumber());
                    Assert.True(NativeMethods.EmptyClipboard());
                    try { write(); }
                    finally { expected = NativeMethods.GetClipboardSequenceNumber(); }
                }
                finally { NativeMethods.CloseClipboard(); }
                Assert.Equal(owner.Handle, NativeMethods.GetClipboardOwner());
                expected = NativeMethods.GetClipboardSequenceNumber();
            }
            void RoundTrip(string name, Action seed, Action verify)
            {
                Seed(seed);
                Assert.Equal(expected, NativeMethods.GetClipboardSequenceNumber());
                using var lease = Wait(transaction.BeginTemporaryTextAsync("Grüße 👋\r\nComplete transcript", CancellationToken.None));
                Assert.True(transaction.IsCurrent(lease));
                expected = NativeMethods.GetClipboardSequenceNumber();
                Assert.Equal("Grüße 👋\r\nComplete transcript", ReadText());
                Assert.Equal(ClipboardRestoreResult.Restored, Wait(transaction.RestoreAsync(lease, CancellationToken.None)));
                expected = NativeMethods.GetClipboardSequenceNumber();
                verify();
                output.WriteLine("PASS " + name);
            }

            RoundTrip("empty clipboard", () => { }, () => Open(() => Assert.Equal(0u, NativeMethods.EnumClipboardFormats(0))));
            var text = Encoding.Unicode.GetBytes("Previous text: äöü 👋\0");
            RoundTrip("Unicode text", () => Put(NativeMethods.CF_UNICODETEXT, text), () => Assert.Equal("Previous text: äöü 👋", ReadText()));
            var pixels = new byte[] { 0x11, 0x22, 0x33, 0xFF, 0x44, 0x55, 0x66, 0xFF };
            RoundTrip("native bitmap pixels", () =>
            {
                var bitmap = CreateBitmap(2, 1, 1, 32, pixels);
                Assert.NotEqual(IntPtr.Zero, bitmap);
                if (NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, bitmap) == IntPtr.Zero)
                { NativeMethods.DeleteObject(bitmap); throw new InvalidOperationException("Bitmap seed failed"); }
            }, () => Open(() =>
            {
                var restored = new byte[pixels.Length];
                Assert.Equal(pixels.Length, GetBitmapBits(NativeMethods.GetClipboardData(NativeMethods.CF_BITMAP), restored.Length, restored));
                Assert.Equal(pixels, restored);
            }));
            var dib = new byte[48];
            BitConverter.GetBytes(40).CopyTo(dib, 0); BitConverter.GetBytes(2).CopyTo(dib, 4);
            BitConverter.GetBytes(1).CopyTo(dib, 8); BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)32).CopyTo(dib, 14); pixels.CopyTo(dib, 40);
            RoundTrip("DIB bitmap bytes", () => Put(NativeMethods.CF_DIB, dib), () => CheckBytes(NativeMethods.CF_DIB, dib));
            var htmlFormat = NativeMethods.RegisterClipboardFormat("HTML Format");
            var rtfFormat = NativeMethods.RegisterClipboardFormat("Rich Text Format");
            var customFormat = NativeMethods.RegisterClipboardFormat("TypeWhisper.Tests.Binary");
            var html = Encoding.UTF8.GetBytes("<html><body><!--StartFragment--><b>Grüße</b><!--EndFragment--></body></html>\0");
            var rtf = Encoding.ASCII.GetBytes("{\\rtf1\\ansi previous \\b text}\0");
            var binary = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();
            RoundTrip("HTML + RTF + opaque binary + Unicode", () =>
            { Put(htmlFormat, html); Put(rtfFormat, rtf); Put(customFormat, binary); Put(NativeMethods.CF_UNICODETEXT, text); },
            () => { CheckBytes(htmlFormat, html); CheckBytes(rtfFormat, rtf); CheckBytes(customFormat, binary); CheckBytes(NativeMethods.CF_UNICODETEXT, text); });
            // Paths are fixture metadata only: no files are opened, created or executed.
            var paths = Encoding.Unicode.GetBytes("C:\\TypeWhisper Test\\Überblick 🧪.zip\0C:\\TypeWhisper Test\\script.ps1\0C:\\TypeWhisper Test\\file without extension\0\0");
            var drop = new byte[20 + paths.Length];
            BitConverter.GetBytes(20).CopyTo(drop, 0); BitConverter.GetBytes(1).CopyTo(drop, 16); paths.CopyTo(drop, 20);
            var effect = NativeMethods.RegisterClipboardFormat("Preferred DropEffect");
            RoundTrip("file-drop paths with Unicode/spaces/odd extensions and move effect", () =>
            { Put(NativeMethods.CF_HDROP, drop); Put(effect, BitConverter.GetBytes(2)); },
            () => { CheckBytes(NativeMethods.CF_HDROP, drop); CheckBytes(effect, BitConverter.GetBytes(2)); });

            foreach (var name in new[] { "System.Drawing.Bitmap", "FileContents", "TypeWhisper.Tests.Unrenderable" })
            {
                var format = NativeMethods.RegisterClipboardFormat(name);
                Seed(() => { Put(NativeMethods.CF_UNICODETEXT, text); NativeMethods.SetClipboardData(format, IntPtr.Zero); });
                var before = NativeMethods.GetClipboardSequenceNumber();
                Assert.ThrowsAny<Exception>(() => Wait(transaction.BeginTemporaryTextAsync("must not replace", CancellationToken.None)));
                Assert.Equal(before, NativeMethods.GetClipboardSequenceNumber());
                Assert.Equal("Previous text: äöü 👋", ReadText());
                output.WriteLine("PASS unrenderable " + name + " aborts without replacing clipboard");
            }

            var fileContents = NativeMethods.RegisterClipboardFormat("FileContents");
            RoundTrip("Explorer file drop plus unavailable indexed FileContents", () =>
            {
                // Enumeration order must not matter: FileContents comes first.
                NativeMethods.SetClipboardData(fileContents, IntPtr.Zero);
                Put(NativeMethods.CF_HDROP, drop);
                Put(effect, BitConverter.GetBytes(2));
            }, () =>
            {
                CheckBytes(NativeMethods.CF_HDROP, drop);
                CheckBytes(effect, BitConverter.GetBytes(2));
            });

            Seed(() => Put(NativeMethods.CF_UNICODETEXT, text));
            using var guarded = Wait(transaction.BeginTemporaryTextAsync("temporary", CancellationToken.None));
            expected = NativeMethods.GetClipboardSequenceNumber();
            Seed(() => Put(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes("newer user copy simulation\0")));
            Assert.Equal(ClipboardRestoreResult.ClipboardChanged, Wait(transaction.RestoreAsync(guarded, CancellationToken.None)));
            Assert.Equal("newer user copy simulation", ReadText());
            output.WriteLine("PASS newer clipboard is not overwritten");

            Seed(() => Put(NativeMethods.CF_UNICODETEXT, text));
            var payload = "First line: Grüße 👋\r\nSecond line: <> & Ω";
            using (var pasteLease = Wait(transaction.BeginTemporaryTextAsync(payload, CancellationToken.None)))
            {
                expected = NativeMethods.GetClipboardSequenceNumber();
                var plain = new System.Windows.Controls.TextBox { AcceptsReturn = true };
                plain.Paste();
                Assert.Equal(payload, plain.Text);
                var rich = new System.Windows.Controls.RichTextBox();
                rich.Paste();
                var richText = new System.Windows.Documents.TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
                Assert.Equal(payload, richText.TrimEnd('\r', '\n'));
                Assert.Equal(ClipboardRestoreResult.Restored, Wait(transaction.RestoreAsync(pasteLease, CancellationToken.None)));
                expected = NativeMethods.GetClipboardSequenceNumber();
                Assert.Equal(payload, plain.Text);
                Assert.Equal("Previous text: äöü 👋", ReadText());
                output.WriteLine("PASS complete Unicode/multiline paste into WPF TextBox and RichTextBox, retained after clipboard restore");
            }

            // A real OLE data object with stream-backed virtual-file representations.
            // No real file is written or launched.
            var fileBytes = Encoding.UTF8.GetBytes("Virtual file payload: Grüße 🧪");
            var descriptor = new byte[596];
            BitConverter.GetBytes(1).CopyTo(descriptor, 0);
            BitConverter.GetBytes(0x44).CopyTo(descriptor, 4);
            BitConverter.GetBytes(0x80).CopyTo(descriptor, 40);
            BitConverter.GetBytes(fileBytes.Length).CopyTo(descriptor, 72);
            Encoding.Unicode.GetBytes("Überblick 🧪.odd\0").CopyTo(descriptor, 76);
            var ole = new System.Windows.DataObject();
            ole.SetData("FileGroupDescriptorW", new System.IO.MemoryStream(descriptor), false);
            ole.SetData("FileContents", new System.IO.MemoryStream(fileBytes), false);
            ole.SetData(System.Windows.DataFormats.UnicodeText, "OLE virtual file fixture", false);
            System.Windows.Clipboard.SetDataObject(ole, false);
            expected = NativeMethods.GetClipboardSequenceNumber();
            var oleBefore = expected;
            IClipboardLease? virtualLease = null;
            try
            {
                virtualLease = Wait(transaction.BeginTemporaryTextAsync("dictated over virtual file", CancellationToken.None));
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                Assert.Equal(oleBefore, NativeMethods.GetClipboardSequenceNumber());
                Assert.Equal("OLE virtual file fixture", ReadText());
                output.WriteLine("PASS OLE virtual-file provider safely rejected without replacing contents");
            }
            if (virtualLease is not null)
            {
                using (virtualLease)
                {
                    expected = NativeMethods.GetClipboardSequenceNumber();
                    Assert.Equal(ClipboardRestoreResult.Restored, Wait(transaction.RestoreAsync(virtualLease, CancellationToken.None)));
                    expected = NativeMethods.GetClipboardSequenceNumber();
                    CheckBytes(NativeMethods.RegisterClipboardFormat("FileGroupDescriptorW"), descriptor);
                    CheckBytes(NativeMethods.RegisterClipboardFormat("FileContents"), fileBytes);
                    output.WriteLine("PASS real OLE virtual-file descriptor and stream contents restored byte-for-byte");
                }
            }
        }
        finally
        {
            // Only accept OUR last known sequence, never the current arbitrary sequence.
            transaction.AcceptSequence(original, expected);
            var result = Wait(transaction.RestoreAsync(original, CancellationToken.None));
            output.WriteLine(result == ClipboardRestoreResult.Restored
                ? "Private test clipboard restored; interactive clipboard untouched." : "Private clipboard changed; newer test content preserved.");
        }
    }

    private static void Put(uint format, byte[] bytes)
    {
        var handle = NativeMethods.GlobalAlloc(0x42, (nuint)bytes.Length);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            var pointer = NativeMethods.GlobalLock(handle);
            Assert.NotEqual(IntPtr.Zero, pointer);
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { NativeMethods.GlobalUnlock(handle); }
            Assert.NotEqual(IntPtr.Zero, NativeMethods.SetClipboardData(format, handle));
            handle = IntPtr.Zero;
        }
        finally { if (handle != IntPtr.Zero) NativeMethods.GlobalFree(handle); }
    }
    private static void Open(Action action)
    {
        Assert.True(TryOpen(IntPtr.Zero));
        try { action(); } finally { NativeMethods.CloseClipboard(); }
    }
    private static bool TryOpen(IntPtr owner)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner)) return true;
            Wait(Task.Delay(50).ContinueWith(_ => true));
        }
        return false;
    }
    private static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
    private static string? ReadText()
    {
        string? result = null;
        Open(() =>
        {
            var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            var pointer = NativeMethods.GlobalLock(handle);
            Assert.NotEqual(IntPtr.Zero, pointer);
            try { result = Marshal.PtrToStringUni(pointer); } finally { NativeMethods.GlobalUnlock(handle); }
        });
        return result;
    }
    private static void CheckBytes(uint format, byte[] expected) => Open(() =>
    {
        var handle = NativeMethods.GetClipboardData(format);
        Assert.True(GlobalSize(handle) >= (nuint)expected.Length);
        var pointer = NativeMethods.GlobalLock(handle);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try { var actual = new byte[expected.Length]; Marshal.Copy(pointer, actual, 0, actual.Length); Assert.Equal(expected, actual); }
        finally { NativeMethods.GlobalUnlock(handle); }
    });
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);
    [DllImport("gdi32.dll")] private static extern int GetBitmapBits(IntPtr bitmap, int length, byte[] data);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr handle);
}
