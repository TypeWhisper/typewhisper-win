using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

public sealed class Reson8ResilienceTests : PortableMigration.Tests.TranscriptionResilienceTests
{
    protected override ITranscriptionEnginePlugin Create(HttpClient client) => new Reson8Plugin(client);
}
