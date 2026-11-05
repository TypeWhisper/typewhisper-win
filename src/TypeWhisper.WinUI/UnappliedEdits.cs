using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

// Settings controls whose changes wait for an explicit Apply. Settings does not rebuild a page over such a change.
internal static class UnappliedEdits
{
    private static readonly ConditionalWeakTable<FrameworkElement, Func<bool>> Pending = new();

    // Raised on the UI thread when a tracked change was applied or dropped, so a rebuild that waited for it can run.
    internal static event Action? Settled;

    internal static void Track(FrameworkElement owner, Func<bool> pending) => Pending.AddOrUpdate(owner, pending);

    internal static void NotifySettled() => Settled?.Invoke();

    internal static bool Any(IEnumerable<FrameworkElement> elements) =>
        elements.Any(element => Pending.TryGetValue(element, out var pending) && pending());
}
