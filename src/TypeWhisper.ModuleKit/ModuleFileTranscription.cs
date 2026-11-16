using Microsoft.UI.Xaml;

namespace TypeWhisper.ModuleKit;

/// <summary>Content shown with the options of the file transcription queue.</summary>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="CreateContent">
/// Creates the content once, when the file transcription view first shows its options. If it throws, nothing is shown.
/// </param>
public sealed record ModuleFileTranscriptionOption(string Id, Func<UIElement> CreateContent);

/// <summary>A handler called after a file transcription result was accepted.</summary>
/// <remarks>
/// It covers files transcribed in the file transcription view. Files from the watched folder and transcriptions
/// requested through the local HTTP API do not call it.
/// </remarks>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Completed">Runs on the UI thread once per accepted result, after History has saved it.</param>
public sealed record ModuleFileTranscriptionHook(string Id, Action<ModuleFileTranscription> Completed);

/// <summary>An accepted file transcription result.</summary>
/// <param name="RecordId">The identifier of the History record, or null when History did not save the result.</param>
/// <param name="SourcePath">The full path of the transcribed file.</param>
/// <param name="Text">The text as transcribed and corrected.</param>
/// <param name="Language">The language code, or null when unknown.</param>
/// <param name="DurationSeconds">The length of the audio.</param>
/// <param name="Segments">The timed segments the engine returned, if any.</param>
public sealed record ModuleFileTranscription(
    string? RecordId,
    string SourcePath,
    string Text,
    string? Language,
    double DurationSeconds,
    IReadOnlyList<ModuleTimedText> Segments);

/// <summary>A piece of transcribed text with its time in the audio.</summary>
/// <param name="Text">The segment's text as the engine returned it.</param>
/// <param name="StartSeconds">Where the segment starts.</param>
/// <param name="EndSeconds">Where the segment ends.</param>
public sealed record ModuleTimedText(string Text, double StartSeconds, double EndSeconds);
