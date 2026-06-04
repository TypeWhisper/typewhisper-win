namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents transcription segment data.
/// </summary>
/// <param name="Text">Text supplied to the member.</param>
/// <param name="Start">Start supplied to the member.</param>
/// <param name="End">End supplied to the member.</param>
public sealed record TranscriptionSegment(string Text, double Start, double End);
