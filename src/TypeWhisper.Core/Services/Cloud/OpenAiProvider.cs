using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Cloud;

public sealed class OpenAiProvider : CloudProviderBase
{
    public override string Id => "openai";
    public override string DisplayName => "OpenAI";
    public override string BaseUrl => "https://api.openai.com";
    public override string? TranslationModel => "gpt-4o-mini";

    public override IReadOnlyList<CloudModelInfo> TranscriptionModels { get; } =
    [
        new()
        {
            Id = "whisper-1",
            DisplayName = "Whisper 1",
            ApiModelName = "whisper-1",
        },
        new()
        {
            Id = "gpt-4o-transcribe",
            DisplayName = "GPT-4o Transcribe",
            ApiModelName = "gpt-4o-transcribe",
            SupportsTranslation = false,
            ResponseFormat = "json",
        },
        new()
        {
            Id = "gpt-4o-mini-transcribe",
            DisplayName = "GPT-4o Mini Transcribe",
            ApiModelName = "gpt-4o-mini-transcribe",
            SupportsTranslation = false,
            ResponseFormat = "json",
        }
    ];
}
