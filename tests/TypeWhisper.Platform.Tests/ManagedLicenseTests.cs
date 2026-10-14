using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.Platform.Tests;

// Managed activation against a fake Polar: keys starting with SUP- are supporter keys, every other key is commercial.
public sealed class ManagedLicenseTests : IDisposable
{
    private const string CommercialBenefit = "5138b20a-57ba-48aa-a664-2139cd6df0de";
    private const string SupporterBenefit = "0c695b7a-2f3a-4797-81c7-1410dbb76cc2";
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task ManagedKey_ActivatesOnceAndReusesTheActivationAcrossLaunches()
    {
        var polar = new FakePolar();
        var directory = CreateTempDir();
        var first = Create(polar, directory);
        var notifications = 0;
        first.StatusChanged += () => notifications++;

        Assert.True(await first.ReconcileManagedLicenseAsync("  COM-ORG-1  "));

        Assert.True(first.IsLicenseManaged);
        Assert.False(first.ManagedLicenseFailed);
        Assert.True(first.HasCommercialLicense);
        Assert.Equal("COM-ORG-1", GetPrivateField<string>(first, "_commercialLicenseKey"));
        Assert.Equal(["activate:COM-ORG-1", "validate:COM-ORG-1"], polar.Operations);
        Assert.True(notifications > 0);

        var second = Create(polar, directory);
        Assert.True(await second.ReconcileManagedLicenseAsync("COM-ORG-1"));
        await second.ValidateAllIfNeededAsync();

        Assert.True(second.IsLicenseManaged);
        Assert.True(second.HasCommercialLicense);
        Assert.Equal(2, polar.Operations.Count);
        Assert.Empty(polar.DeactivatedActivations);
    }

    [Fact]
    public async Task ManagedKey_ReplacesADifferentActivationAfterTheNewOneIsActive()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        await service.ActivateCommercialLicenseAsync("COM-MANUAL");
        Assert.True(service.HasCommercialLicense);

        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-2"));

        Assert.Equal("COM-ORG-2", GetPrivateField<string>(service, "_commercialLicenseKey"));
        Assert.Equal("activation-2", GetPrivateField<string>(service, "_commercialActivationId"));
        Assert.Equal(
            ["activate:COM-MANUAL", "validate:COM-MANUAL", "activate:COM-ORG-2", "validate:COM-ORG-2", "deactivate:COM-MANUAL"],
            polar.Operations);
        Assert.Equal(["activation-1"], polar.DeactivatedActivations);
        Assert.True(service.HasCommercialLicense);
        Assert.False(service.ManagedLicenseFailed);
    }

    [Fact]
    public async Task ManagedKey_FailedReplacementKeepsThePreviousLicense()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        await service.ActivateCommercialLicenseAsync("COM-MANUAL");
        polar.FailActivation = true;

        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-3"));

        Assert.True(service.IsLicenseManaged);
        Assert.True(service.ManagedLicenseFailed);
        Assert.True(service.HasCommercialLicense);
        Assert.Equal("COM-MANUAL", GetPrivateField<string>(service, "_commercialLicenseKey"));
        Assert.Equal("activation-1", GetPrivateField<string>(service, "_commercialActivationId"));
        Assert.Equal("activate:COM-ORG-3", polar.Operations.Last());
        Assert.DoesNotContain("validate:COM-ORG-3", polar.Operations);
        Assert.Empty(polar.DeactivatedActivations);
        Assert.Null(service.CommercialActivationError);

        polar.FailActivation = false;
        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-3"));

        Assert.False(service.ManagedLicenseFailed);
        Assert.Equal("COM-ORG-3", GetPrivateField<string>(service, "_commercialLicenseKey"));
        Assert.Equal(["activation-1"], polar.DeactivatedActivations);
    }

    [Fact]
    public async Task ManagedKey_RejectsSupporterKeysAndReleasesTheirActivation()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        await service.ActivateCommercialLicenseAsync("COM-MANUAL");

        Assert.True(await service.ReconcileManagedLicenseAsync("SUP-ORG"));

        Assert.True(service.ManagedLicenseFailed);
        Assert.True(service.HasCommercialLicense);
        Assert.Equal("COM-MANUAL", GetPrivateField<string>(service, "_commercialLicenseKey"));
        Assert.False(service.HasSupporterActivation);
        Assert.Equal(["activation-2"], polar.DeactivatedActivations);
    }

    [Fact]
    public async Task ManagedKey_RemovedValueEndsManagedModeAndKeepsTheActivation()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-4"));
        var notifications = 0;
        service.StatusChanged += () => notifications++;

        Assert.False(await service.ReconcileManagedLicenseAsync(null));

        Assert.False(service.IsLicenseManaged);
        Assert.False(service.ManagedLicenseFailed);
        Assert.True(service.HasCommercialLicense);
        Assert.Equal(1, notifications);
        Assert.Empty(polar.DeactivatedActivations);
        Assert.Equal(2, polar.Operations.Count);

        Assert.False(await service.ReconcileManagedLicenseAsync("   "));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ManagedKey_RecreatesAnActivationThatIsGone()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-5"));
        // A validation that confirmed the activation as deleted clears the record.
        SetPrivateField(service, "_commercialActivationId", null);
        Assert.False(service.HasCommercialActivation);

        Assert.True(await service.ReconcileManagedLicenseAsync("COM-ORG-5"));

        Assert.True(service.HasCommercialLicense);
        Assert.Equal("activation-2", GetPrivateField<string>(service, "_commercialActivationId"));
        Assert.Equal(["activate:COM-ORG-5", "validate:COM-ORG-5", "activate:COM-ORG-5", "validate:COM-ORG-5"], polar.Operations);
        Assert.Empty(polar.DeactivatedActivations);
    }

    [Fact]
    public async Task ManagedKey_PropagatesCancellation()
    {
        var polar = new FakePolar();
        var service = Create(polar, CreateTempDir());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcileManagedLicenseAsync("COM-ORG-6", cancellation.Token));

        Assert.True(service.IsLicenseManaged);
        Assert.False(service.HasCommercialActivation);
        Assert.Empty(polar.Operations);
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirs)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private LicenseService Create(FakePolar polar, string directory) =>
        new(new HttpClient(new Handler(polar)) { Timeout = TimeSpan.FromSeconds(5) }, directory);

    private string CreateTempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TypeWhisperManagedLicenseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        return directory;
    }

    private static T? GetPrivateField<T>(LicenseService service, string name) where T : class
    {
        var field = typeof(LicenseService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(service) as T;
    }

    private static void SetPrivateField(LicenseService service, string name, object? value)
    {
        var field = typeof(LicenseService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(service, value);
    }

    private sealed class FakePolar
    {
        private int _activations;

        public List<string> Operations { get; } = [];
        public List<string> DeactivatedActivations { get; } = [];
        public bool FailActivation { get; set; }

        public HttpResponseMessage Respond(HttpRequestMessage request, string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var key = document.RootElement.GetProperty("key").GetString()!;
            var operation = request.RequestUri!.Segments.Last();
            Operations.Add(operation + ":" + key);
            switch (operation)
            {
                case "activate":
                    if (FailActivation) return Json(HttpStatusCode.InternalServerError, """{"detail":"unavailable"}""");
                    return Json(HttpStatusCode.OK, $$"""{"id":"activation-{{++_activations}}"}""");
                case "validate":
                    var benefit = key.StartsWith("SUP-", StringComparison.Ordinal) ? SupporterBenefit : CommercialBenefit;
                    return Json(HttpStatusCode.OK, $$"""{"status":"granted","benefit_id":"{{benefit}}"}""");
                case "deactivate":
                    DeactivatedActivations.Add(document.RootElement.GetProperty("activation_id").GetString()!);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    throw new InvalidOperationException(operation);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Handler(FakePolar polar) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return polar.Respond(request, body);
        }
    }
}
