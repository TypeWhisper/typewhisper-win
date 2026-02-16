using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Cloud;

public sealed class GroqProvider : CloudProviderBase
{
    public override string Id => "groq";
    public override string DisplayName => "Groq";
    public override string BaseUrl => "https://api.groq.com/openai";
    public override string? TranslationModel => "llama-3.3-70b-versatile";

    public override IReadOnlyList<CloudModelInfo> TranscriptionModels { get; } =
    [
        new()
        {
            Id = "whisper-large-v3",
            DisplayName = "Whisper Large V3",
            ApiModelName = "whisper-large-v3",
        },
        new()
        {
            Id = "whisper-large-v3-turbo",
            DisplayName = "Whisper Large V3 Turbo",
            ApiModelName = "whisper-large-v3-turbo",
            SupportsTranslation = false,
        }
    ];
}
