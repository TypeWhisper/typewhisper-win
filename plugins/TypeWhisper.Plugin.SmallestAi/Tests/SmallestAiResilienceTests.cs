using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.PluginSDK;

public sealed class SmallestAiResilienceTests : PortableMigration.Tests.TranscriptionResilienceTests
{
    protected override ITranscriptionEnginePlugin Create(HttpClient client) => new SmallestAiPlugin(client);
}
