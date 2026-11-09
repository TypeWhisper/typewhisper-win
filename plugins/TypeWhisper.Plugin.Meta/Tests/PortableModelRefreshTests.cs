using System.Net;
using System.Text;
using TypeWhisper.Plugin.Meta;
using TypeWhisper.Plugin.Meta.Tests;
namespace PortableMigration.Tests;

public sealed class PortableModelRefreshTests
{
    [Fact]
    public async Task DamagedFetchDateReadsAsNoDateAndRefreshes()
    {
        using var fixture = new PortableFixture();
        fixture.Secrets.Values["api-key"] = "meta-key";
        fixture.Host.SetSetting("fetchedModelsAtUtc", "not a date");
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var handler = new CatalogHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-1.0"}]}""", Encoding.UTF8, "application/json"),
        }));
        using var plugin = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new FixedClock(now));

        await plugin.ActivateAsync(fixture.Host);
        await plugin.ModelRefreshTask;

        Assert.Equal(1, handler.Requests);
        Assert.Equal(now, fixture.Host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        await plugin.DeactivateAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
