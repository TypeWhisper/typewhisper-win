using System.Text;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.GemmaLocal;

internal sealed class GemmaOutputBuffer
{
    private static readonly string[] StopMarkers = ["<end_of_turn>", "<eos>"];
    private static readonly int MarkerOverlap = StopMarkers.Max(marker => marker.Length) - 1;
    private readonly StringBuilder _text = new();
    private bool _stopped;

    internal bool Append(string fragment)
    {
        if (_stopped) return true;
        var start = Math.Max(0, _text.Length - MarkerOverlap);
        _text.Append(fragment);
        var tail = _text.ToString(start, _text.Length - start);
        var firstMarker = -1;
        foreach (var marker in StopMarkers)
        {
            var index = tail.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (firstMarker < 0 || index < firstMarker)) firstMarker = index;
        }
        if (firstMarker < 0) return false;
        _text.Length = start + firstMarker;
        _stopped = true;
        return true;
    }

    internal string Finish(bool endOfGeneration)
    {
        if (!_stopped && !endOfGeneration)
            throw new PluginRequestException("Gemma 3 stopped the workflow response at its token limit.",
                PluginRequestFailureKind.OutputTruncated, isTransient: false);
        return _text.ToString().Trim();
    }
}
