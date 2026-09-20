using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.WinUI.Platform;
using Loc = TypeWhisper.WinUI.LicenseText;

namespace TypeWhisper.Platform.Tests;

public sealed class LicenseServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LicenseRequests_PinVersionWithoutChangingSharedClient(bool supporter)
    {
        var operations = new List<string>();
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "example.com")
            {
                Assert.False(request.Headers.Contains("Polar-Version"));
                return Json(HttpStatusCode.OK, "{}");
            }

            Assert.Equal("2026-04", Assert.Single(request.Headers.GetValues("Polar-Version")));
            Assert.Equal(HttpMethod.Post, request.Method);
            var operation = request.RequestUri.Segments.Last();
            operations.Add(operation);
            return operation switch
            {
                "activate" => Json(HttpStatusCode.OK, """{"id":"test-activation"}"""),
                "validate" => GrantedLicense(supporter),
                "deactivate" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(operation)
            };
        }));
        var service = new LicenseService(client, CreateTempDir());
        Assert.NotNull(await service.ActivateAnyLicenseKeyAsync("TEST-ONLY"));
        if (supporter)
        {
            await service.RefreshSupporterLicenseAsync();
            await service.DeactivateSupporterLicenseAsync();
        }
        else
        {
            await service.RefreshCommercialLicenseAsync();
            await service.DeactivateCommercialLicenseAsync();
        }

        using var unrelatedResponse = await client.GetAsync("https://example.com/unrelated");
        Assert.False(client.DefaultRequestHeaders.Contains("Polar-Version"));
        Assert.Equal(["activate", "validate", "validate", "deactivate"], operations);
        Assert.False(service.HasCommercialActivation);
        Assert.False(service.HasSupporterActivation);
    }

    public static IEnumerable<object?[]> AmbiguousPolarErrors()
    {
        var errors = new (string Body, string? Version, HttpStatusCode Status)[]
        {
            // Observed sandbox response for both malformed and unknown versions.
            ("""{"detail":"Not Found"}""", null, HttpStatusCode.NotFound),
            ("""{"error":"UnsupportedAPIVersion","detail":"Version not found"}""", null, HttpStatusCode.NotFound),
            ("""{"error":"RemovedAPIVersion","detail":"Version does not exist"}""", null, HttpStatusCode.NotFound),
            ("""{"detail":"Not found"}""", "2026-04", HttpStatusCode.NotFound),
            ("""{"type":"ResourceNotFound","detail":"Not found"}""", "2026-04", HttpStatusCode.NotFound),
            ("""{"error":"ResourceNotFound","detail":"Not found"}""", null, HttpStatusCode.NotFound),
            ("""{"error":"ResourceNotFound","detail":"Not found"}""", "2026-10", HttpStatusCode.NotFound),
            ("""{"detail":[{"type":"unknown_version"}]}""", null, HttpStatusCode.NotFound),
            ("<html>Not found</html>", null, HttpStatusCode.NotFound),
            ("", null, HttpStatusCode.NotFound),
            ("", null, HttpStatusCode.TemporaryRedirect),
            ("", null, HttpStatusCode.PermanentRedirect),
            ("""{"error":"ResourceNotFound","detail":"Not found"}""", "2026-04", HttpStatusCode.InternalServerError),
            ("""{"detail":"No LicenseKeyActivation does not exist"}""", "2026-04", HttpStatusCode.BadRequest),
        };
        foreach (var supporter in new[] { false, true })
        foreach (var error in errors)
            yield return [supporter, error.Body, error.Version, error.Status];
    }

    [Theory]
    [MemberData(nameof(AmbiguousPolarErrors))]
    public async Task AmbiguousErrors_PreserveStoredLicenseAcrossOperationsAndRestart(
        bool supporter, string body, string? version, HttpStatusCode status)
    {
        var fail = false;
        var calls = 0;
        using var client = new HttpClient(new CapturingHandler((request, payload) =>
        {
            calls++;
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("TEST-ONLY", document.RootElement.GetProperty("key").GetString());
            if (!request.RequestUri!.AbsolutePath.EndsWith("/activate"))
                Assert.Equal("test-activation", document.RootElement.GetProperty("activation_id").GetString());
            return fail ? PolarError(status, body, version)
                : request.RequestUri.AbsolutePath.EndsWith("/activate")
                    ? Json(HttpStatusCode.OK, """{"id":"test-activation"}""")
                    : GrantedLicense(supporter);
        }));
        var directory = CreateTempDir();
        var service = new LicenseService(client, directory);
        Assert.NotNull(await service.ActivateAnyLicenseKeyAsync("TEST-ONLY"));
        var stored = File.ReadAllBytes(Path.Combine(directory, "licenses.dat"));
        var proof = Assert.Single(service.GetDiscordClaimProofCandidates());
        fail = true;

        if (supporter)
        {
            await service.ValidateSupporterAsync();
            await service.RefreshSupporterLicenseAsync();
            Assert.NotNull(service.SupporterRefreshError);
            await service.DeactivateSupporterLicenseAsync();
            Assert.NotNull(service.SupporterDeactivationError);
            await service.ActivateSupporterKeyAsync("TEST-ONLY");
            Assert.NotNull(service.SupporterActivationError);
        }
        else
        {
            await service.ValidateCommercialLicenseAsync();
            await service.RefreshCommercialLicenseAsync();
            Assert.NotNull(service.CommercialRefreshError);
            await service.DeactivateCommercialLicenseAsync();
            Assert.NotNull(service.CommercialDeactivationError);
            await service.ActivateCommercialLicenseAsync("TEST-ONLY");
            Assert.NotNull(service.CommercialActivationError);
        }

        if (status == HttpStatusCode.NotFound)
            Assert.Equal(Loc.Instance["License.ApiCompatibilityError"],
                supporter ? service.SupporterRefreshError : service.CommercialRefreshError);
        Assert.Equal(6, calls); // No retries against an unpinned Current contract.
        Assert.Equal(proof, Assert.Single(service.GetDiscordClaimProofCandidates()));
        Assert.Equal(stored, File.ReadAllBytes(Path.Combine(directory, "licenses.dat")));
        var reloaded = new LicenseService(client, directory);
        Assert.Equal(proof, Assert.Single(reloaded.GetDiscordClaimProofCandidates()));
        Assert.Equal(LicenseStatus.Active, supporter ? reloaded.SupporterStatus : reloaded.CommercialStatus);
    }

    [Theory]
    [InlineData(false, "Not found")]
    [InlineData(true, "Not found")]
    [InlineData(false, "License key is no longer active.")]
    [InlineData(true, "License key is no longer active.")]
    [InlineData(false, "License key has expired.")]
    [InlineData(true, "License key has expired.")]
    public async Task ConfirmedLicenseErrors_ClearStoredState(bool supporter, string detail)
    {
        var fail = false;
        using var client = new HttpClient(new CapturingHandler((request, _) => fail
            ? PolarError(HttpStatusCode.NotFound, JsonSerializer.Serialize(new { error = "ResourceNotFound", detail }), "2026-04")
            : request.RequestUri!.AbsolutePath.EndsWith("/activate")
                ? Json(HttpStatusCode.OK, """{"id":"test-activation"}""")
                : GrantedLicense(supporter)));
        var directory = CreateTempDir();
        var service = new LicenseService(client, directory);
        Assert.NotNull(await service.ActivateAnyLicenseKeyAsync("TEST-ONLY"));
        fail = true;

        if (supporter)
            await service.RefreshSupporterLicenseAsync();
        else
            await service.RefreshCommercialLicenseAsync();

        Assert.False(service.HasSupporterActivation);
        Assert.False(service.HasCommercialActivation);
        Assert.False(new LicenseService(client, directory).HasCommercialActivation);
        Assert.False(new LicenseService(client, directory).HasSupporterActivation);
        Assert.Equal(LicenseStatus.Unlicensed, supporter ? service.SupporterStatus : service.CommercialStatus);
    }

    [Theory]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    public async Task InactiveValidation_MarksExpiredAndRetainsActivation(bool supporter, string status)
    {
        var service = CreateService((_, _) => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { status })));
        SeedStoredActivation(service, supporter, DateTime.UtcNow.AddDays(-40));
        await service.ValidateAllIfNeededAsync();
        Assert.Equal(LicenseStatus.Expired, supporter ? service.SupporterStatus : service.CommercialStatus);
        Assert.True(supporter ? service.HasSupporterActivation : service.HasCommercialActivation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OfflineValidation_PreservesEntitlementAndRetryInterval(bool supporter, bool timeout)
    {
        var calls = 0;
        var service = CreateService((_, _) =>
        {
            calls++;
            if (timeout) throw new TaskCanceledException("Timeout");
            throw new HttpRequestException("Not found");
        });
        SeedStoredActivation(service, supporter, DateTime.UtcNow);
        await service.ValidateAllIfNeededAsync();
        Assert.Equal(0, calls);
        SetPrivateField(service, supporter ? "_supporterLastValidated" : "_commercialLastValidated", DateTime.UtcNow.AddDays(-40));
        await service.ValidateAllIfNeededAsync();
        await service.ValidateAllIfNeededAsync();
        Assert.Equal(2, calls);
        Assert.Equal(LicenseStatus.Active, supporter ? service.SupporterStatus : service.CommercialStatus);
        Assert.True(supporter ? service.HasSupporterActivation : service.HasCommercialActivation);
    }

    private static void SeedStoredActivation(LicenseService service, bool supporter, DateTime lastValidated)
    {
        var prefix = supporter ? "_supporter" : "_commercial";
        SetPrivateField(service, prefix + "LicenseKey", "TEST-ONLY");
        SetPrivateField(service, prefix + "ActivationId", "test-activation");
        SetPrivateField(service, prefix + "LastValidated", lastValidated);
        if (supporter)
        {
            service.SupporterStatus = LicenseStatus.Active;
            service.SupporterTier = SupporterTier.Gold;
        }
        else
        {
            service.CommercialStatus = LicenseStatus.Active;
            service.CommercialTier = CommercialLicenseTier.Team;
        }
    }

    private static HttpResponseMessage GrantedLicense(bool supporter) => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            status = "granted",
            benefit_id = supporter ? "0c695b7a-2f3a-4797-81c7-1410dbb76cc2" : "5138b20a-57ba-48aa-a664-2139cd6df0de"
        }));

    private static HttpResponseMessage PolarError(HttpStatusCode status, string body, string? version)
    {
        var response = Json(status, body);
        if (status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            response.Headers.Location = new Uri("https://example.com/redirected-license-request");
        if (version is not null)
            response.Headers.Add("Polar-Version", version);
        return response;
    }

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
            LicenseService.DetectCommercialTier("legacy", "Legacy commercial license for 3 devices"));
        Assert.Null(
            LicenseService.DetectCommercialTier("legacy", "Legacy commercial license for 2 devices"));
        Assert.Null(
            LicenseService.DetectCommercialTier("legacy", "Legacy commercial license for 13 devices"));
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
        var service = CreateService((request, _) => PolarError(
            HttpStatusCode.NotFound,
            """{"error":"ResourceNotFound","detail":"Not found"}""", "2026-04"));
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
        var service = CreateService((request, _) => PolarError(
            HttpStatusCode.NotFound,
            """{"error":"ResourceNotFound","detail":"Not found"}""", "2026-04"));
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
    public async Task ActivatedLicensePersistsEncryptedAndCanBeDeactivatedAfterRestart()
    {
        var directory = CreateTempDir();
        var requests = new List<string>();
        var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            requests.Add(route);
            return route.Split('/').Last() switch
            {
                "activate" => Json(HttpStatusCode.OK, """{"id":"device-test"}"""),
                "validate" => Json(HttpStatusCode.OK, """{"status":"granted","benefit_id":"a4c0b152-0b91-4588-b8f8-779870affba9"}"""),
                "deactivate" => Json(HttpStatusCode.OK, "{}"),
                _ => throw new InvalidOperationException(route)
            };
        }));
        var service = new LicenseService(client, directory);
        Assert.NotNull(await service.ActivateAnyLicenseKeyAsync("TEST-ONLY-NOT-A-REAL-KEY"));
        Assert.Null(service.StorageError);
        Assert.DoesNotContain("TEST-ONLY-NOT-A-REAL-KEY", File.ReadAllText(Path.Combine(directory, "licenses.dat")));
        var reloaded = new LicenseService(client, directory);
        Assert.True(reloaded.HasCommercialLicense);
        await reloaded.DeactivateCommercialLicenseAsync();
        Assert.False(reloaded.HasCommercialActivation);
        Assert.False(new LicenseService(client, directory).HasCommercialLicense);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task ActivationReportsCredentialPersistenceFailure()
    {
        var directory = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(directory, "licenses.dat"));
        var service = new LicenseService(new HttpClient(new CapturingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/activate")
                ? Json(HttpStatusCode.OK, """{"id":"test-device"}""")
                : Json(HttpStatusCode.OK, """{"status":"granted","benefit_id":"a4c0b152-0b91-4588-b8f8-779870affba9"}"""))), directory);
        await service.ActivateAnyLicenseKeyAsync("TEST-ONLY-NOT-A-REAL-KEY");
        Assert.NotNull(service.StorageError);
        Assert.DoesNotContain("TEST-ONLY-NOT-A-REAL-KEY", service.StorageError);
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
