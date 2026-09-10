using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;

// Interactive test fixture only. Sends NO input. Computer Use performs the target-app paste.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var owner = new HwndSource(new HwndSourceParameters("Clipboard diagnostic owner") { ParentWindow = new(-3), WindowStyle = 0 });
        using var transaction = new WindowsClipboardTransaction(owner.Handle);
        IClipboardLease? backup = null;
        IClipboardLease? staged = null;
        uint expected = 0;
        Console.WriteLine("READY: commands backup, files, stage, restore, finish. No microphone or keyboard input.");
        for (string? command; (command = Console.ReadLine()) is not null;)
        {
            try
            {
                switch (command)
                {
                    case "inspect":
                        // Invoke the unchanged production snapshot routine without clearing
                        // or replacing clipboard data. Dispose only duplicated handles.
                        var before = NativeMethods.GetClipboardSequenceNumber();
                        if (!NativeMethods.OpenClipboard(owner.Handle)) throw new InvalidOperationException("Clipboard busy; nothing changed");
                        try
                        {
                            var enterprise = NativeMethods.RegisterClipboardFormat(WindowsClipboardTransaction.EnterpriseDataProtectionId);
                            var capture = typeof(WindowsClipboardTransaction).GetMethod("CaptureSnapshotCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            try
                            {
                                using var snapshot = (IDisposable)capture.Invoke(transaction, [enterprise])!;
                                Console.WriteLine("SNAPSHOT_OK: production snapshot captured without replacing clipboard");
                            }
                            catch (System.Reflection.TargetInvocationException ex)
                            {
                                Console.WriteLine("SNAPSHOT_FAILED: " + ex.InnerException?.Message);
                            }
                        }
                        finally
                        {
                            NativeMethods.CloseClipboard();
                            Console.WriteLine("CLIPBOARD_SEQUENCE_UNCHANGED: " + (before == NativeMethods.GetClipboardSequenceNumber()));
                        }
                        break;
                    case "backup":
                        if (backup is not null) throw new InvalidOperationException("Already backed up");
                        backup = Wait(transaction.BeginTemporaryTextAsync("TypeWhisper probe baseline", CancellationToken.None));
                        expected = NativeMethods.GetClipboardSequenceNumber();
                        Console.WriteLine("BACKUP_OK");
                        break;
                    case "files":
                        if (backup is null || staged is not null || expected != NativeMethods.GetClipboardSequenceNumber()) throw new InvalidOperationException("Unsafe clipboard state");
                        var files = new System.Collections.Specialized.StringCollection();
                        files.Add(Path.GetFullPath("tests/TypeWhisper.Clipboard.Probe/TypeWhisper.Clipboard.Probe.csproj"));
                        files.Add(Path.GetFullPath("tests/TypeWhisper.Clipboard.Probe/Program.cs"));
                        if (files.Cast<string>().Any(p => !File.Exists(p))) throw new FileNotFoundException("Fixture missing");
                        System.Windows.Clipboard.SetFileDropList(files);
                        expected = NativeMethods.GetClipboardSequenceNumber();
                        Console.WriteLine("FILES_READY: two existing repository fixture files; none opened or executed");
                        break;
                    case "stage":
                        if (backup is null || staged is not null || expected != NativeMethods.GetClipboardSequenceNumber()) throw new InvalidOperationException("Unsafe clipboard state");
                        staged = Wait(transaction.BeginTemporaryTextAsync("TypeWhisper Notepad probe: Grüße 123.", CancellationToken.None));
                        expected = NativeMethods.GetClipboardSequenceNumber();
                        Console.WriteLine("STAGED: use Ctrl+V in the observed empty test editor, then restore");
                        break;
                    case "restore":
                        if (staged is null) throw new InvalidOperationException("Nothing staged");
                        Wait(Task.Delay(500).ContinueWith(_ => true));
                        var result = Wait(transaction.RestoreAsync(staged, CancellationToken.None));
                        staged.Dispose(); staged = null;
                        if (result == ClipboardRestoreResult.Restored) expected = NativeMethods.GetClipboardSequenceNumber();
                        Console.WriteLine("RESTORE: " + result);
                        break;
                    case "finish":
                        if (staged is not null) throw new InvalidOperationException("Restore staged data first");
                        if (backup is not null)
                        {
                            transaction.AcceptSequence(backup, expected);
                            Console.WriteLine("BACKUP_RESTORE: " + Wait(transaction.RestoreAsync(backup, CancellationToken.None)));
                            backup.Dispose(); backup = null;
                        }
                        return;
                }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message + " Backup retained; process stays alive for retry."); }
        }
        // Never silently discard a still-owned backup after a disconnected controller.
        if (backup is not null) throw new InvalidOperationException("Controller disconnected before restoration.");
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
}
