using System.Windows.Interop;
using System.Windows.Media;

namespace TypeWhisper.Windows.Services;

internal static class WpfRenderingSafety
{
    internal const int RenderThreadFailureHResult = unchecked((int)0x88980406);

    public static void EnableBeforeAnyWindow() =>
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

    public static bool IsRenderThreadFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == RenderThreadFailureHResult)
                return true;
        }

        return false;
    }
}
