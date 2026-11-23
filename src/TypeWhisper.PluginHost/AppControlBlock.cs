using System.ComponentModel;
using TypeWhisper.Core;

namespace TypeWhisper.PluginHost;

/// <summary>
/// Recognizes plugin files that Windows refused to load or start because of Smart App Control or an App Control
/// for Business policy. The plugin's own message would ask for a reinstall, which cannot help: the files are intact,
/// Windows only does not allow unsigned code on this PC.
/// </summary>
public static class AppControlBlock
{
    // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION, and the variants that add Smart App Control's reputation verdict:
    // malicious, potentially unwanted, a dangerous file from the web, and reputation service unreachable.
    private static readonly int[] ErrorCodes = [4551, 4556, 4557, 4558, 4559];

    // NativeLibrary.Load in this app reports a refused DLL only by Windows' text for the error, without its code. The
    // loader takes that text from the system in the user's language, as Win32Exception does here.
    private static readonly Lazy<string[]> SystemMessages = new(() => OperatingSystem.IsWindows()
        ? ErrorCodes.Select(code => new Win32Exception(code).Message.Trim()).Where(text => text.Length > 0).ToArray()
        : []);

    /// <summary>The explanation shown instead of the plugin's message.</summary>
    public static string Message => Loc.T("Windows blocked a file this plugin needs. Smart App Control or an App Control policy on this PC blocks unsigned code, and the plugin's native runtime is not signed. Use a cloud provider instead; on a managed PC, your administrator can allow the plugin.");

    /// <summary>True when <paramref name="error"/> or an exception it wraps is such a block.</summary>
    public static bool IsBlock(Exception? error) => Find(error) is not null;

    /// <summary>
    /// Returns the exception in <paramref name="error"/>'s chain that reports the block; its message names the
    /// blocked file, which a plugin's own wrapping message may leave out.
    /// </summary>
    public static Exception? Find(Exception? error)
    {
        // Bounded, as a plugin's exception chain is not trusted to end.
        for (var depth = 0; error is not null && depth < 32; depth++, error = error.InnerException)
        {
            if (error is AppControlBlockedException) return error;
            if (error is Win32Exception win32 && ErrorCodes.Contains(win32.NativeErrorCode)) return error;
            foreach (var code in ErrorCodes)
            {
                var hresult = unchecked((int)0x80070000) | code;
                // The 1.2 app (.NET 10) added the loader's result to that text, as "(0x800711C7)" (issue #823).
                if (error.HResult == hresult || error.Message.Contains($"0x{hresult:X8}", StringComparison.OrdinalIgnoreCase)) return error;
            }
            if (SystemMessages.Value.Any(text => error.Message.Contains(text, StringComparison.OrdinalIgnoreCase))) return error;
            if (error is AggregateException aggregate) return aggregate.InnerExceptions.Select(Find).FirstOrDefault(found => found is not null);
        }
        return null;
    }
}

/// <summary>
/// Windows blocked one of a plugin's native files; see <see cref="AppControlBlock"/>. It is an
/// <see cref="InvalidOperationException"/>, as the worker's errors without a kind of their own are.
/// </summary>
public sealed class AppControlBlockedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
