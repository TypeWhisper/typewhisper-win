using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LicenseServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void CommercialTierInference_MapsKnownPolarBenefitIdsAndLegacyDescriptions()
    {
        Assert.Equal(
            CommercialLicenseTier.Individual,
            LicenseService.DetectCommercialTier("a4c0b152-0b91-4588-b8f8-779870affba9", "Individual Business License"));
        Assert.Equal(
            CommercialLicenseTier.Team,
            LicenseService.DetectCommercialTier("afc8fac1-0e8f-4bb7-a1bc-60c8250b9923", "Lifetime Team Business License"));
        Assert.Equal(
            CommercialLicenseTier.Enterprise,
            LicenseService.DetectCommercialTier("40b82917-f74e-4cc3-8165-937f1f47b294", "Enterprise Business License"));

        Assert.Equal(
            CommercialLicenseTier.Individual,
            LicenseService.DetectCommercialTier("legacy", "Freelancer single-seat license for 2 devices"));
        Assert.Equal(
            CommercialLicenseTier.Team,
            LicenseService.DetectCommercialTier("legacy", "Small teams up to 10 devices"));
        Assert.Equal(
            CommercialLicenseTier.Enterprise,
            LicenseService.DetectCommercialTier("legacy", "Unlimited devices and priority support"));
    }

    [Fact]
    public void SupporterTierInference_MapsKnownPolarBenefitIdsAndLegacyDescriptions()
    {
        Assert.Equal(
            SupporterTier.Bronze,
            LicenseService.DetectSupporterTier("d3eef5ed-bc8c-469d-809b-79fdfe5fc8e8", "Supporter Bronze License"));
        Assert.Equal(
            SupporterTier.Silver,
            LicenseService.DetectSupporterTier("9ca12e41-b407-4368-9745-76b72ff2c7c2", "Supporter Silver License"));
        Assert.Equal(
            SupporterTier.Gold,
            LicenseService.DetectSupporterTier("0c695b7a-2f3a-4797-81c7-1410dbb76cc2", "Supporter Gold License"));

        Assert.Equal(SupporterTier.Bronze, LicenseService.DetectSupporterTier("legacy", "Bronze supporter"));
        Assert.Equal(SupporterTier.Silver, LicenseService.DetectSupporterTier("legacy", "Silver supporter"));
        Assert.Equal(SupporterTier.Gold, LicenseService.DetectSupporterTier("legacy", "Gold supporter"));
    }

    [Fact]
    public void CheckoutUrlBuilder_AddsWindowsPolarAttribution()
    {
        var url = new Uri(PolarCheckoutUrlBuilder.BuildAppCheckoutUrl(
            "https://buy.polar.sh/example",
            "settings_individual_monthly"));

        Assert.Contains("utm_source=typewhisper_windows", url.Query);
        Assert.Contains("utm_medium=app", url.Query);
        Assert.Contains("utm_content=windows_settings_individual_monthly", url.Query);
    }

    [Theory]
    [InlineData(AppDistributionKind.Direct, "direct")]
    [InlineData(AppDistributionKind.Store, "store")]
    public async Task ActivationPayload_IncludesPlatformVersionAndDistribution(
        AppDistributionKind distributionKind,
        string expectedDistribution)
    {
        string? activationBody = null;
        var service = CreateService(
            (request, body) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v1/customer-portal/license-keys/activate")
                    activationBody = body;

                return request.RequestUri?.AbsolutePath switch
                {
                    "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-123"}"""),
                    "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-123","status":"granted","expires_at":null,"benefit_id":"40b82917-f74e-4cc3-8165-937f1f47b294"}"""),
                    _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
                };
            },
            distributionKind,
            "9.8.7-test");

        await service.ActivateAnyLicenseKeyAsync("TYPEWHISPER-COM-123");

        using var payload = JsonDocument.Parse(Assert.IsType<string>(activationBody));
        var root = payload.RootElement;
        Assert.Equal(Environment.MachineName, root.GetProperty("label").GetString());
        Assert.Equal("windows", root.GetProperty("meta").GetProperty("platform").GetString());
        Assert.Equal("9.8.7-test", root.GetProperty("meta").GetProperty("app_version").GetString());
        Assert.Equal(expectedDistribution, root.GetProperty("meta").GetProperty("distribution").GetString());
    }

    [Fact]
    public async Task ActivateAnyLicenseKeyAsync_RoutesCommercialBenefitIntoCommercialState()
    {
        var service = CreateService((request, _) => request.RequestUri?.AbsolutePath switch
        {
            "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-123"}"""),
            "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-123","status":"granted","expires_at":null,"benefit_id":"40b82917-f74e-4cc3-8165-937f1f47b294"}"""),
            _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
        });

        var entitlement = await service.ActivateAnyLicenseKeyAsync("TYPEWHISPER-COM-123");

        Assert.Equal(ActivatedLicenseEntitlementKind.Commercial, entitlement?.Kind);
        Assert.Equal(CommercialLicenseTier.Enterprise, entitlement?.CommercialTier);
        Assert.True(entitlement?.IsLifetime);
        Assert.Equal(LicenseStatus.Active, service.CommercialStatus);
        Assert.Equal(CommercialLicenseTier.Enterprise, service.CommercialTier);
        Assert.True(service.HasCommercialLicense);
        Assert.False(service.HasSupporterLicense);
    }

    [Fact]
    public async Task ActivateAnyLicenseKeyAsync_RoutesSupporterBenefitIntoSupporterState()
    {
        var service = CreateService((request, _) => request.RequestUri?.AbsolutePath switch
        {
            "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-999"}"""),
            "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-999","status":"granted","expires_at":"2027-01-01T00:00:00Z","benefit_id":"0c695b7a-2f3a-4797-81c7-1410dbb76cc2"}"""),
            _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
        });

        var entitlement = await service.ActivateAnyLicenseKeyAsync("TYPEWHISPER-SUP-999");

        Assert.Equal(ActivatedLicenseEntitlementKind.Supporter, entitlement?.Kind);
        Assert.Equal(SupporterTier.Gold, entitlement?.SupporterTier);
        Assert.Equal(LicenseStatus.Active, service.SupporterStatus);
        Assert.Equal(SupporterTier.Gold, service.SupporterTier);
        Assert.False(service.HasCommercialLicense);
        Assert.True(service.HasSupporterLicense);
    }

    [Fact]
    public async Task CommercialActivation_RejectsSupporterKeyAndDeactivatesNewActivation()
    {
        var deactivateCalls = 0;
        var service = CreateService((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-999"}"""),
                "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-999","status":"granted","benefit_id":"d3eef5ed-bc8c-469d-809b-79fdfe5fc8e8"}"""),
                "/v1/customer-portal/license-keys/deactivate" => CountedJson(ref deactivateCalls),
                _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
            };
        });

        await service.ActivateCommercialLicenseAsync("TYPEWHISPER-SUP-999");

        Assert.Equal(LicenseStatus.Unlicensed, service.CommercialStatus);
        Assert.False(service.HasCommercialLicense);
        Assert.Contains("supporter tier", service.CommercialActivationError);
        Assert.Equal(1, deactivateCalls);
    }

    [Fact]
    public async Task SupporterActivation_RejectsCommercialKeyAndDeactivatesNewActivation()
    {
        var deactivateCalls = 0;
        var service = CreateService((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-123"}"""),
                "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-123","status":"granted","benefit_id":"5138b20a-57ba-48aa-a664-2139cd6df0de"}"""),
                "/v1/customer-portal/license-keys/deactivate" => CountedJson(ref deactivateCalls),
                _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
            };
        });

        await service.ActivateSupporterKeyAsync("TYPEWHISPER-COM-123");

        Assert.Equal(LicenseStatus.Unlicensed, service.SupporterStatus);
        Assert.False(service.HasSupporterLicense);
        Assert.Contains("commercial license", service.SupporterActivationError);
        Assert.Equal(1, deactivateCalls);
    }

    [Fact]
    public async Task ActivateAnyLicenseKeyAsync_UnknownBenefitDoesNotActivateAndDeactivatesNewActivation()
    {
        var deactivateCalls = 0;
        var service = CreateService((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-unknown"}"""),
                "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-unknown","status":"granted","benefit_id":"unknown-benefit"}"""),
                "/v1/customer-portal/license-keys/deactivate" => CountedJson(ref deactivateCalls),
                _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
            };
        });

        var entitlement = await service.ActivateAnyLicenseKeyAsync("TYPEWHISPER-UNK-1");

        Assert.Null(entitlement);
        Assert.Equal(LicenseStatus.Unlicensed, service.CommercialStatus);
        Assert.Equal(LicenseStatus.Unlicensed, service.SupporterStatus);
        Assert.Contains("known TypeWhisper entitlement", service.LicenseActivationError);
        Assert.Equal(1, deactivateCalls);
    }

    [Fact]
    public async Task ActivateAnyLicenseKeyAsync_PropagatesCancellation()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ActivateAnyLicenseKeyAsync("TYPEWHISPER-COM-123", cts.Token));

        Assert.Null(service.LicenseActivationError);
    }

    [Fact]
    public async Task ValidateCommercialLicenseAsync_MovesStoredSupporterActivationOutOfCommercialSlot()
    {
        var service = CreateService((request, _) => Json(
            HttpStatusCode.OK,
            """{"id":"activation-999","status":"granted","benefit_id":"9ca12e41-b407-4368-9745-76b72ff2c7c2"}"""));
        SetPrivateField(service, "_commercialLicenseKey", "TYPEWHISPER-SUP-999");
        SetPrivateField(service, "_commercialActivationId", "activation-999");
        service.CommercialStatus = LicenseStatus.Active;

        await service.ValidateCommercialLicenseAsync();

        Assert.False(service.HasCommercialLicense);
        Assert.False(service.HasCommercialActivation);
        Assert.Equal(LicenseStatus.Active, service.SupporterStatus);
        Assert.Equal(SupporterTier.Silver, service.SupporterTier);
        Assert.True(service.HasSupporterActivation);
    }

    [Fact]
    public async Task ValidateCommercialLicenseAsync_PropagatesCancellation()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        SetPrivateField(service, "_commercialLicenseKey", "TYPEWHISPER-COM-123");
        SetPrivateField(service, "_commercialActivationId", "activation-123");
        service.CommercialStatus = LicenseStatus.Active;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ValidateCommercialLicenseAsync(cts.Token));

        Assert.Equal(LicenseStatus.Active, service.CommercialStatus);
        Assert.True(service.HasCommercialActivation);
    }

    [Fact]
    public async Task ValidateSupporterLicenseAsync_MissingPolarActivationClearsLocalState()
    {
        var service = CreateService((request, _) => Json(
            HttpStatusCode.NotFound,
            """{"type":"ResourceNotFound","detail":"Not found"}"""));
        SetPrivateField(service, "_supporterLicenseKey", "TYPEWHISPER-SUP-999");
        SetPrivateField(service, "_supporterActivationId", "activation-999");
        service.SupporterStatus = LicenseStatus.Active;
        service.SupporterTier = SupporterTier.Gold;

        await service.ValidateSupporterAsync();

        Assert.Equal(LicenseStatus.Unlicensed, service.SupporterStatus);
        Assert.Null(service.SupporterTier);
        Assert.False(service.HasSupporterActivation);
    }

    [Fact]
    public async Task DeactivateCommercialLicenseAsync_MissingPolarActivationClearsLocalStateWithoutError()
    {
        var service = CreateService((request, _) => Json(
            HttpStatusCode.NotFound,
            """{"type":"ResourceNotFound","detail":"Not found"}"""));
        SetPrivateField(service, "_commercialLicenseKey", "TYPEWHISPER-COM-123");
        SetPrivateField(service, "_commercialActivationId", "activation-123");
        service.CommercialStatus = LicenseStatus.Active;
        service.CommercialTier = CommercialLicenseTier.Team;

        await service.DeactivateCommercialLicenseAsync();

        Assert.Equal(LicenseStatus.Unlicensed, service.CommercialStatus);
        Assert.Null(service.CommercialDeactivationError);
        Assert.False(service.HasCommercialActivation);
    }

    [Fact]
    public async Task SupporterDiscordService_RefreshStatusIfNeededClearsStaleClaimActivation()
    {
        var statusCalls = 0;
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        SetPrivateField(service, "_supporterLicenseKey", "TYPEWHISPER-SUP-999");
        SetPrivateField(service, "_supporterActivationId", "activation-current");
        service.SupporterStatus = LicenseStatus.Active;
        service.SupporterTier = SupporterTier.Gold;
        var discord = CreateDiscordService((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/claims/polar/status")
                statusCalls++;

            return Json(HttpStatusCode.OK, """{"status":"linked","discord_username":"marco"}""");
        });
        discord.ClaimState = SupporterDiscordClaimState.Linked;
        discord.ClaimActivationId = "activation-stale";
        discord.SessionId = "session-stale";

        await discord.RefreshStatusIfNeededAsync(service);

        Assert.Equal(0, statusCalls);
        Assert.Equal(SupporterDiscordClaimState.Unavailable, discord.ClaimState);
        Assert.Null(discord.ClaimActivationId);
        Assert.Null(discord.SessionId);
    }

    [Fact]
    public async Task SupporterDiscordService_RefreshClaimStatusUsesCurrentCandidateWhenClaimActivationIdIsWhitespace()
    {
        string? requestedQuery = null;
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        SetPrivateField(service, "_supporterLicenseKey", "TYPEWHISPER-SUP-999");
        SetPrivateField(service, "_supporterActivationId", "activation-current");
        service.SupporterStatus = LicenseStatus.Active;
        service.SupporterTier = SupporterTier.Gold;
        var discord = CreateDiscordService((request, _) =>
        {
            requestedQuery = request.RequestUri?.Query;
            return Json(HttpStatusCode.OK, """{"status":"linked","discord_username":"marco"}""");
        });
        discord.ClaimState = SupporterDiscordClaimState.Linked;
        discord.ClaimActivationId = "   ";
        discord.SessionId = "session-current";

        await discord.RefreshClaimStatusAsync(service);

        Assert.Contains("activation_id=activation-current", requestedQuery);
        Assert.Contains("session_id=session-current", requestedQuery);
        Assert.Equal(SupporterDiscordClaimState.Linked, discord.ClaimState);
        Assert.Equal("marco", discord.DiscordUsername);
    }

    [Fact]
    public async Task LicenseSectionViewModel_ActivateLicenseCommandClearsSharedInputOnSuccess()
    {
        var service = CreateService((request, _) => request.RequestUri?.AbsolutePath switch
        {
            "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-123"}"""),
            "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-123","status":"granted","benefit_id":"5138b20a-57ba-48aa-a664-2139cd6df0de"}"""),
            _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
        });
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService())
        {
            LicenseKeyInput = "TYPEWHISPER-COM-123"
        };

        await viewModel.ActivateLicenseCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.LicenseKeyInput);
        Assert.NotNull(viewModel.LicenseActivationNotice);
        Assert.True(viewModel.ShowCommercialManage);
        Assert.False(viewModel.ShowSupporterManage);
    }

    [Fact]
    public void LicenseSectionViewModel_CustomerPortalShortcutIsVisibleWithoutStoredActivation()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService());

        Assert.False(viewModel.ShowCommercialManage);
        Assert.False(viewModel.ShowSupporterManage);
        Assert.True(viewModel.ShowCustomerPortalShortcut);
        Assert.True(viewModel.OpenCustomerPortalCommand.CanExecute(null));
    }

    [Fact]
    public void LicenseSectionViewModel_ExposesFourSelectablePlanOptions()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService());
        var planOptionsProperty = typeof(LicenseSectionViewModel).GetProperty("PlanOptions");
        var selectPlanCommandProperty = typeof(LicenseSectionViewModel).GetProperty("SelectPlanCommand");

        Assert.NotNull(planOptionsProperty);
        Assert.NotNull(selectPlanCommandProperty);

        var options = ((System.Collections.IEnumerable)planOptionsProperty.GetValue(viewModel)!)
            .Cast<object>()
            .ToList();

        Assert.Equal(["gpl", "individual", "team", "enterprise"], options.Select(option => GetProperty<string>(option, "Id")));
        Assert.True(GetProperty<bool>(options[0], "IsSelected"));
        Assert.False(viewModel.ShowCommercialPurchase);

        var command = (System.Windows.Input.ICommand)selectPlanCommandProperty.GetValue(viewModel)!;
        command.Execute(options.Single(option => GetProperty<string>(option, "Id") == "enterprise"));

        var updatedOptions = ((System.Collections.IEnumerable)planOptionsProperty.GetValue(viewModel)!)
            .Cast<object>()
            .ToList();

        Assert.True(viewModel.IsBusinessUser);
        Assert.True(viewModel.ShowCommercialPurchase);
        Assert.Equal("enterprise", GetProperty<string>(updatedOptions.Single(option => GetProperty<bool>(option, "IsSelected")), "Id"));
    }

    [Fact]
    public void LicenseSectionViewModel_HidesPlanAndActivationInputsWhenCommercialLicenseIsActive()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService());
        viewModel.SelectPlanCommand.Execute(viewModel.PlanOptions.Single(option => option.Id == "enterprise"));
        service.CommercialStatus = LicenseStatus.Active;
        service.CommercialTier = CommercialLicenseTier.Individual;
        service.CommercialIsLifetime = true;

        Assert.False(viewModel.ShowPlanSelection);
        Assert.False(viewModel.ShowLicenseActivation);
        Assert.False(viewModel.ShowCommercialPurchase);
        Assert.Equal(
            ["individual"],
            viewModel.PlanOptions.Where(option => option.IsSelected).Select(option => option.Id));
    }

    [Fact]
    public void LicenseSectionViewModel_ManagementSectionsFollowStoredActivations()
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, "{}"));
        SetPrivateField(service, "_commercialLicenseKey", "TYPEWHISPER-COM-123");
        SetPrivateField(service, "_commercialActivationId", "activation-123");
        SetPrivateField(service, "_supporterLicenseKey", "TYPEWHISPER-SUP-999");
        SetPrivateField(service, "_supporterActivationId", "activation-999");
        service.CommercialStatus = LicenseStatus.Expired;
        service.SupporterStatus = LicenseStatus.Expired;
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService());

        Assert.True(viewModel.ShowCommercialManage);
        Assert.True(viewModel.ShowSupporterManage);
        Assert.False(viewModel.ShowCustomerPortalShortcut);
        Assert.True(viewModel.ShowCommercialPurchase);
        Assert.True(viewModel.ShowSupporterPurchase);
        Assert.False(viewModel.ShowDiscordSection);
    }

    [Fact]
    public async Task LicenseSectionViewModel_RefreshErrorIsBindable()
    {
        var service = CreateService((_, _) => Json(
            HttpStatusCode.InternalServerError,
            """{"detail":"Polar is down"}"""));
        SetPrivateField(service, "_commercialLicenseKey", "TYPEWHISPER-COM-123");
        SetPrivateField(service, "_commercialActivationId", "activation-123");
        service.CommercialStatus = LicenseStatus.Active;
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService());

        await viewModel.RefreshCommercialLicenseCommand.ExecuteAsync(null);

        Assert.Equal("Polar is down", service.CommercialRefreshError);
    }

    [Fact]
    public async Task LicenseSectionViewModel_DeactivateCommercialLicenseCommandClearsActivationNotice()
    {
        var service = CreateService((request, _) => request.RequestUri?.AbsolutePath switch
        {
            "/v1/customer-portal/license-keys/activate" => Json(HttpStatusCode.OK, """{"id":"activation-123"}"""),
            "/v1/customer-portal/license-keys/validate" => Json(HttpStatusCode.OK, """{"id":"activation-123","status":"granted","benefit_id":"4eb5fa60-ed43-475d-a9b1-c837e67307e5"}"""),
            "/v1/customer-portal/license-keys/deactivate" => Json(HttpStatusCode.OK, """{"ok":true}"""),
            _ => Json(HttpStatusCode.InternalServerError, """{"detail":"unexpected"}"""),
        });
        var viewModel = new LicenseSectionViewModel(service, CreateDiscordService())
        {
            LicenseKeyInput = "TYPEWHISPER-COM-123"
        };
        await viewModel.ActivateLicenseCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.LicenseActivationNotice);

        await viewModel.DeactivateCommercialLicenseCommand.ExecuteAsync(null);

        Assert.Null(viewModel.LicenseActivationNotice);
        Assert.False(viewModel.ShowCommercialManage);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private LicenseService CreateService(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder,
        AppDistributionKind? distributionKind = null,
        string? appVersion = null)
    {
        var tempDir = CreateTempDir();

        return new LicenseService(
            new HttpClient(new CapturingHandler(responder)) { Timeout = TimeSpan.FromSeconds(5) },
            tempDir,
            distributionKind,
            appVersion);
    }

    private SupporterDiscordService CreateDiscordService(Func<HttpRequestMessage, string, HttpResponseMessage>? responder = null)
    {
        var tempDir = CreateTempDir();
        return new SupporterDiscordService(
            new HttpClient(new CapturingHandler(responder ?? ((_, _) => Json(HttpStatusCode.OK, "{}"))))
            {
                Timeout = TimeSpan.FromSeconds(5)
            },
            tempDir);
    }

    private string CreateTempDir()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var suiteRoot = Path.GetFullPath("TypeWhisperLicenseTests", tempRoot);
        var tempDir = Path.GetFullPath(Guid.NewGuid().ToString("N"), suiteRoot);
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        return tempDir;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage CountedJson(ref int calls)
    {
        calls++;
        return Json(HttpStatusCode.OK, """{"ok":true}""");
    }

    private static void SetPrivateField<T>(LicenseService service, string fieldName, T value)
    {
        var field = typeof(LicenseService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(service, value);
    }

    private static T GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responder(request, body);
        }
    }
}
