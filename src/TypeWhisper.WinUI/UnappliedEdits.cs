using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

// Settings controls whose changes wait for an explicit Apply. Settings does not rebuild a page over such a change.
internal static class UnappliedEdits
{
    private static readonly ConditionalWeakTable<FrameworkElement, Func<bool>> Pending = new();

    internal static void Track(FrameworkElement owner, Func<bool> pending) => Pending.AddOrUpdate(owner, pending);

    internal static bool Any(IEnumerable<FrameworkElement> elements) =>
        elements.Any(element => Pending.TryGetValue(element, out var pending) && pending());
}
