using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages commercial and supporter licenses via Polar.sh.
/// Mirrors the macOS split between business/commercial licensing and supporter status.
/// </summary>
public sealed partial class LicenseService : ObservableObject
{
    private const string BaseUrl = "https://api.polar.sh/v1/customer-portal/license-keys";
    private const string OrganizationId = "96de503c-3c8b-4d08-9ded-c7f6e20fdde4";
    private const string CredentialStoreFileName = "licenses.dat";
    private const string LegacyCredentialFileName = "license.json";
    private static readonly byte[] Entropy = "TypeWhisper.License.v2"u8.ToArray();
    private static readonly TimeSpan CommercialValidationInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan SupporterValidationInterval = TimeSpan.FromDays(30);
    private static readonly Dictionary<string, CommercialLicenseTier> KnownCommercialBenefitIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a4c0b152-0b91-4588-b8f8-779870affba9"] = CommercialLicenseTier.Individual,
        ["4eb5fa60-ed43-475d-a9b1-c837e67307e5"] = CommercialLicenseTier.Individual,
        ["5138b20a-57ba-48aa-a664-2139cd6df0de"] = CommercialLicenseTier.Team,
        ["afc8fac1-0e8f-4bb7-a1bc-60c8250b9923"] = CommercialLicenseTier.Team,
        ["40b82917-f74e-4cc3-8165-937f1f47b294"] = CommercialLicenseTier.Enterprise,
        ["1857c2ed-3f80-4a8a-93c7-c1d67e02db2e"] = CommercialLicenseTier.Enterprise,
    };

    private static readonly Dictionary<string, SupporterTier> KnownSupporterBenefitIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d3eef5ed-bc8c-469d-809b-79fdfe5fc8e8"] = global::TypeWhisper.Windows.Services.SupporterTier.Bronze,
        ["9ca12e41-b407-4368-9745-76b72ff2c7c2"] = global::TypeWhisper.Windows.Services.SupporterTier.Silver,
        ["0c695b7a-2f3a-4797-81c7-1410dbb76cc2"] = global::TypeWhisper.Windows.Services.SupporterTier.Gold,
    };

    private readonly HttpClient _http;
    private readonly string _credentialPath;
    private readonly string _legacyCredentialPath;

    private bool _suppressPersistence;
    private string? _commercialLicenseKey;
    private string? _commercialActivationId;
    private DateTime? _commercialLastValidated;
    private string? _supporterLicenseKey;
    private string? _supporterActivationId;
    private DateTime? _supporterLastValidated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrivateUser))]
    [NotifyPropertyChangedFor(nameof(IsBusinessUser))]
    [NotifyPropertyChangedFor(nameof(ShouldShowReminder))]
    private LicenseUserType _userType = LicenseUserType.PrivateUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommercialLicense))]
    [NotifyPropertyChangedFor(nameof(CommercialTierDisplayName))]
    private LicenseStatus _commercialStatus = LicenseStatus.Unlicensed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommercialTierDisplayName))]
    private CommercialLicenseTier? _commercialTier;

    [ObservableProperty]
    private bool _commercialIsLifetime;

    [ObservableProperty]
    private bool _isLicenseActivating;

    [ObservableProperty]
    private string? _licenseActivationError;

    [ObservableProperty]
    private bool _isCommercialActivating;

    [ObservableProperty]
    private string? _commercialActivationError;

    [ObservableProperty]
    private string? _commercialDeactivationError;

    [ObservableProperty]
    private bool _isCommercialRefreshing;

    [ObservableProperty]
    private string? _commercialRefreshError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupporterLicense))]
    [NotifyPropertyChangedFor(nameof(IsSupporter))]
    [NotifyPropertyChangedFor(nameof(SupporterBadgeTier))]
    [NotifyPropertyChangedFor(nameof(SupporterTierDisplayName))]
    private LicenseStatus _supporterStatus = LicenseStatus.Unlicensed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupporterLicense))]
    [NotifyPropertyChangedFor(nameof(IsSupporter))]
    [NotifyPropertyChangedFor(nameof(SupporterBadgeTier))]
    [NotifyPropertyChangedFor(nameof(SupporterTierDisplayName))]
    private SupporterTier? _supporterTier;

    [ObservableProperty]
    private bool _isSupporterActivating;

    [ObservableProperty]
    private string? _supporterActivationError;

    [ObservableProperty]
    private string? _supporterDeactivationError;

    [ObservableProperty]
    private bool _isSupporterRefreshing;

    [ObservableProperty]
    private string? _supporterRefreshError;

    public event Action? StatusChanged;

    public LicenseService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, TypeWhisperEnvironment.DataPath)
    {
    }

    internal LicenseService(HttpClient http, string dataPath)
    {
        _http = http;
        _credentialPath = ResolveDataFilePath(dataPath, CredentialStoreFileName);
        _legacyCredentialPath = ResolveDataFilePath(dataPath, LegacyCredentialFileName);
        LoadStore();
    }

    public bool IsPrivateUser => UserType == LicenseUserType.PrivateUser;
    public bool IsBusinessUser => UserType == LicenseUserType.Business;
    public bool HasCommercialLicense => CommercialStatus == LicenseStatus.Active;
    public bool HasSupporterLicense => SupporterStatus == LicenseStatus.Active;
    public bool HasCommercialActivation => !string.IsNullOrWhiteSpace(_commercialLicenseKey) && !string.IsNullOrWhiteSpace(_commercialActivationId);
    public bool HasSupporterActivation => !string.IsNullOrWhiteSpace(_supporterLicenseKey) && !string.IsNullOrWhiteSpace(_supporterActivationId);
    public bool IsSupporter => SupporterStatus == LicenseStatus.Active && EffectiveSupporterTier is not null;
    public bool ShouldShowReminder => IsBusinessUser && !HasCommercialLicense;
    public SupporterTier SupporterBadgeTier => EffectiveSupporterTier ?? global::TypeWhisper.Windows.Services.SupporterTier.None;

    public string? CommercialTierDisplayName => CommercialTier switch
    {
        CommercialLicenseTier.Individual => "Individual",
        CommercialLicenseTier.Team => "Team",
        CommercialLicenseTier.Enterprise => "Enterprise",
        _ => null
    };

    public string? SupporterTierDisplayName => EffectiveSupporterTier switch
    {
        global::TypeWhisper.Windows.Services.SupporterTier.Bronze => "Bronze",
        global::TypeWhisper.Windows.Services.SupporterTier.Silver => "Silver",
        global::TypeWhisper.Windows.Services.SupporterTier.Gold => "Gold",
        _ => null
    };

    public SupporterClaimProof? SupporterClaimProof =>
        IsSupporter && !string.IsNullOrWhiteSpace(_supporterLicenseKey) && !string.IsNullOrWhiteSpace(_supporterActivationId)
            ? new SupporterClaimProof(_supporterLicenseKey!, _supporterActivationId!, EffectiveSupporterTier!.Value)
            : null;

    public IReadOnlyList<SupporterClaimProof> GetDiscordClaimProofCandidates()
    {
        var proofs = new List<SupporterClaimProof>(2);

        if (SupporterClaimProof is { } supporterProof)
            proofs.Add(supporterProof);

        if (CommercialStatus == LicenseStatus.Active &&
            !string.IsNullOrWhiteSpace(_commercialLicenseKey) &&
            !string.IsNullOrWhiteSpace(_commercialActivationId))
        {
            var commercialProof = new SupporterClaimProof(
                _commercialLicenseKey!,
                _commercialActivationId!,
                EffectiveSupporterTier ?? global::TypeWhisper.Windows.Services.SupporterTier.Bronze);

            if (!proofs.Any(p => p.Key == commercialProof.Key && p.ActivationId == commercialProof.ActivationId))
                proofs.Add(commercialProof);
        }

        return proofs;
    }

    private SupporterTier? EffectiveSupporterTier => SupporterTier switch
    {
        null => null,
        global::TypeWhisper.Windows.Services.SupporterTier.None when SupporterStatus == LicenseStatus.Active
            => global::TypeWhisper.Windows.Services.SupporterTier.Bronze,
        var tier => tier,
    };

    public void SetUserType(LicenseUserType type)
    {
        UserType = type;
        PersistStore();
        NotifyStateChanged();
    }

    public async Task<ActivatedLicenseEntitlement?> ActivateAnyLicenseKeyAsync(string key, CancellationToken ct = default)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        ct.ThrowIfCancellationRequested();
        IsLicenseActivating = true;
        LicenseActivationError = null;
        CommercialActivationError = null;
        CommercialDeactivationError = null;
        SupporterActivationError = null;
        SupporterDeactivationError = null;

        try
        {
            return await ActivateKeyAsync(trimmed, ExpectedLicenseEntitlementKind.Any, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            LicenseActivationError = ex.Message;
            return null;
        }
        finally
        {
            IsLicenseActivating = false;
        }
    }

    public async Task ActivateCommercialLicenseAsync(string key, CancellationToken ct = default)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        ct.ThrowIfCancellationRequested();
        IsCommercialActivating = true;
        CommercialActivationError = null;
        CommercialDeactivationError = null;
        LicenseActivationError = null;

        try
        {
            await ActivateKeyAsync(trimmed, ExpectedLicenseEntitlementKind.Commercial, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            CommercialActivationError = ex.Message;
        }
        finally
        {
            IsCommercialActivating = false;
        }
    }

    public Task ValidateCommercialLicenseAsync(CancellationToken ct = default) =>
        ValidateCommercialLicenseCoreAsync(reportErrors: false, ct);

    public async Task RefreshCommercialLicenseAsync(CancellationToken ct = default)
    {
        if (!HasCommercialActivation)
            return;

        IsCommercialRefreshing = true;
        CommercialRefreshError = null;

        try
        {
            await ValidateCommercialLicenseCoreAsync(reportErrors: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            CommercialRefreshError = ex.Message;
        }
        finally
        {
            IsCommercialRefreshing = false;
        }
    }

    public async Task ValidateCommercialIfNeededAsync(CancellationToken ct = default)
    {
        if (!HasCommercialActivation)
        {
            if (CommercialStatus != LicenseStatus.Unlicensed || CommercialTier is not null || CommercialIsLifetime)
            {
                ResetCommercialState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
            }

            return;
        }

        if (CommercialStatus != LicenseStatus.Active ||
            !_commercialLastValidated.HasValue ||
            DateTime.UtcNow - _commercialLastValidated.Value > CommercialValidationInterval)
        {
            await ValidateCommercialLicenseAsync(ct);
        }
    }

    public async Task DeactivateCommercialLicenseAsync(CancellationToken ct = default)
    {
        if (!HasCommercialActivation)
            return;

        var key = _commercialLicenseKey!;
        var activationId = _commercialActivationId!;
        CommercialDeactivationError = null;

        try
        {
            await DeactivateCoreAsync(key, activationId, ct);
            ResetCommercialState(clearSecrets: true);
            PersistStore();
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            if (IsPolarResourceMissing(ex))
            {
                ResetCommercialState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
            }
            else
            {
                CommercialDeactivationError = ex.Message;
            }
        }
    }

    public async Task ActivateSupporterKeyAsync(string key, CancellationToken ct = default)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        ct.ThrowIfCancellationRequested();
        IsSupporterActivating = true;
        SupporterActivationError = null;
        SupporterDeactivationError = null;
        LicenseActivationError = null;

        try
        {
            await ActivateKeyAsync(trimmed, ExpectedLicenseEntitlementKind.Supporter, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            SupporterActivationError = ex.Message;
        }
        finally
        {
            IsSupporterActivating = false;
        }
    }

    public Task ValidateSupporterAsync(CancellationToken ct = default) =>
        ValidateSupporterCoreAsync(reportErrors: false, ct);

    public async Task RefreshSupporterLicenseAsync(CancellationToken ct = default)
    {
        if (!HasSupporterActivation)
            return;

        IsSupporterRefreshing = true;
        SupporterRefreshError = null;

        try
        {
            await ValidateSupporterCoreAsync(reportErrors: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            SupporterRefreshError = ex.Message;
        }
        finally
        {
            IsSupporterRefreshing = false;
        }
    }

    public async Task ValidateSupporterIfNeededAsync(CancellationToken ct = default)
    {
        if (!HasSupporterActivation)
        {
            if (SupporterStatus != LicenseStatus.Unlicensed || SupporterTier is not null)
            {
                ResetSupporterState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
            }

            return;
        }

        if (SupporterStatus != LicenseStatus.Active ||
            !_supporterLastValidated.HasValue ||
            DateTime.UtcNow - _supporterLastValidated.Value > SupporterValidationInterval)
        {
            await ValidateSupporterAsync(ct);
        }
    }

    public async Task<bool> ReactivateStoredSupporterKeyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_supporterLicenseKey))
            return false;

        var previousActivationId = _supporterActivationId;
        var previousStatus = SupporterStatus;
        var previousTier = SupporterTier;
        var previousLastValidated = _supporterLastValidated;

        await ActivateSupporterKeyAsync(_supporterLicenseKey, ct);
        if (SupporterStatus == LicenseStatus.Active && !string.IsNullOrWhiteSpace(_supporterActivationId))
            return true;

        _supporterActivationId = previousActivationId;
        SupporterStatus = previousStatus;
        SupporterTier = previousTier;
        _supporterLastValidated = previousLastValidated;
        PersistStore();
        NotifyStateChanged();
        return false;
    }

    public async Task DeactivateSupporterLicenseAsync(CancellationToken ct = default)
    {
        if (!HasSupporterActivation)
            return;

        var key = _supporterLicenseKey!;
        var activationId = _supporterActivationId!;
        SupporterDeactivationError = null;

        try
        {
            await DeactivateCoreAsync(key, activationId, ct);
            ResetSupporterState(clearSecrets: true);
            PersistStore();
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            if (IsPolarResourceMissing(ex))
            {
                ResetSupporterState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
            }
            else
            {
                SupporterDeactivationError = ex.Message;
            }
        }
    }

    public async Task ValidateAllIfNeededAsync(CancellationToken ct = default)
    {
        await ValidateCommercialIfNeededAsync(ct);
        await ValidateSupporterIfNeededAsync(ct);
    }

    private async Task<ActivatedLicenseEntitlement> ActivateKeyAsync(
        string key,
        ExpectedLicenseEntitlementKind expectedEntitlement,
        CancellationToken ct)
    {
        var activation = await ActivateCoreAsync(key, ct);
        var activationId = activation.Id
            ?? throw new InvalidOperationException("Activation failed: Polar did not return an activation id.");

        try
        {
            var validation = await ValidateCoreAsync(key, activationId, ct);
            if (!string.Equals(validation.Status, "granted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This entitlement is not active.");

            var entitlement = ClassifyGrantedValidation(validation);
            EnsureExpectedEntitlement(entitlement, expectedEntitlement);
            ApplyActivatedEntitlement(entitlement, key, activationId);
            PersistStore();
            NotifyStateChanged();
            return entitlement;
        }
        catch
        {
            await TryDeactivateCoreAsync(key, activationId, ct);
            throw;
        }
    }

    private async Task ValidateCommercialLicenseCoreAsync(bool reportErrors, CancellationToken ct)
    {
        if (!HasCommercialActivation)
            return;

        ct.ThrowIfCancellationRequested();
        var key = _commercialLicenseKey!;
        var activationId = _commercialActivationId!;

        try
        {
            var validation = await ValidateCoreAsync(key, activationId, ct);
            ApplyStoredCommercialValidation(key, activationId, validation, reportErrors);
            CommercialActivationError = null;
            CommercialRefreshError = null;
            PersistStore();
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            if (IsPolarResourceMissing(ex))
            {
                ResetCommercialState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
                return;
            }

            Debug.WriteLine($"Commercial license validation failed: {ex.Message}");
            if (reportErrors)
                throw;
        }
    }

    private async Task ValidateSupporterCoreAsync(bool reportErrors, CancellationToken ct)
    {
        if (!HasSupporterActivation)
            return;

        ct.ThrowIfCancellationRequested();
        var key = _supporterLicenseKey!;
        var activationId = _supporterActivationId!;

        try
        {
            var validation = await ValidateCoreAsync(key, activationId, ct);
            ApplyStoredSupporterValidation(key, activationId, validation, reportErrors);
            SupporterActivationError = null;
            SupporterRefreshError = null;
            PersistStore();
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            if (IsPolarResourceMissing(ex))
            {
                ResetSupporterState(clearSecrets: true);
                PersistStore();
                NotifyStateChanged();
                return;
            }

            Debug.WriteLine($"Supporter validation failed: {ex.Message}");
            if (reportErrors)
                throw;
        }
    }

    private void ApplyStoredCommercialValidation(
        string key,
        string activationId,
        PolarValidationResponse validation,
        bool reportErrors)
    {
        if (!string.Equals(validation.Status, "granted", StringComparison.OrdinalIgnoreCase))
        {
            MarkCommercialActivationExpired();
            return;
        }

        var entitlement = TryClassifyGrantedValidation(validation);
        if (entitlement is null)
        {
            MarkCommercialActivationExpired();
            if (reportErrors)
                throw new InvalidOperationException("This key could not be matched to a known TypeWhisper entitlement.");
            return;
        }

        if (entitlement.Kind == ActivatedLicenseEntitlementKind.Commercial)
        {
            ApplyActivatedEntitlement(entitlement, key, activationId);
            return;
        }

        ApplyActivatedEntitlement(entitlement, key, activationId);
        ResetCommercialState(clearSecrets: true);
    }

    private void ApplyStoredSupporterValidation(
        string key,
        string activationId,
        PolarValidationResponse validation,
        bool reportErrors)
    {
        if (!string.Equals(validation.Status, "granted", StringComparison.OrdinalIgnoreCase))
        {
            MarkSupporterActivationExpired();
            return;
        }

        var entitlement = TryClassifyGrantedValidation(validation);
        if (entitlement is null)
        {
            MarkSupporterActivationExpired();
            if (reportErrors)
                throw new InvalidOperationException("This key could not be matched to a known TypeWhisper entitlement.");
            return;
        }

        if (entitlement.Kind == ActivatedLicenseEntitlementKind.Supporter)
        {
            ApplyActivatedEntitlement(entitlement, key, activationId);
            return;
        }

        ApplyActivatedEntitlement(entitlement, key, activationId);
        ResetSupporterState(clearSecrets: true);
    }

    private void ApplyActivatedEntitlement(ActivatedLicenseEntitlement entitlement, string key, string activationId)
    {
        switch (entitlement.Kind)
        {
            case ActivatedLicenseEntitlementKind.Commercial:
                _commercialLicenseKey = key;
                _commercialActivationId = activationId;
                CommercialStatus = LicenseStatus.Active;
                CommercialTier = entitlement.CommercialTier;
                CommercialIsLifetime = entitlement.IsLifetime;
                _commercialLastValidated = DateTime.UtcNow;
                CommercialActivationError = null;
                CommercialDeactivationError = null;
                CommercialRefreshError = null;
                break;

            case ActivatedLicenseEntitlementKind.Supporter:
                _supporterLicenseKey = key;
                _supporterActivationId = activationId;
                SupporterStatus = LicenseStatus.Active;
                SupporterTier = entitlement.SupporterTier ?? global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
                _supporterLastValidated = DateTime.UtcNow;
                SupporterActivationError = null;
                SupporterDeactivationError = null;
                SupporterRefreshError = null;
                break;
        }
    }

    private static ActivatedLicenseEntitlement ClassifyGrantedValidation(PolarValidationResponse validation) =>
        TryClassifyGrantedValidation(validation)
        ?? throw new InvalidOperationException("This key could not be matched to a known TypeWhisper entitlement.");

    private static ActivatedLicenseEntitlement? TryClassifyGrantedValidation(PolarValidationResponse validation)
    {
        var benefitId = validation.ResolvedBenefitId;
        var benefitDescription = validation.ResolvedBenefitDescription;

        if (DetectCommercialTier(benefitId, benefitDescription) is { } commercialTier)
            return ActivatedLicenseEntitlement.Commercial(commercialTier, validation.ExpiresAt is null);

        if (DetectSupporterTier(benefitId, benefitDescription) is { } supporterTier)
            return ActivatedLicenseEntitlement.Supporter(supporterTier);

        return null;
    }

    private static void EnsureExpectedEntitlement(
        ActivatedLicenseEntitlement entitlement,
        ExpectedLicenseEntitlementKind expectedEntitlement)
    {
        if (expectedEntitlement == ExpectedLicenseEntitlementKind.Commercial &&
            entitlement.Kind != ActivatedLicenseEntitlementKind.Commercial)
        {
            throw new InvalidOperationException("This key belongs to a supporter tier, not a commercial license.");
        }

        if (expectedEntitlement == ExpectedLicenseEntitlementKind.Supporter &&
            entitlement.Kind != ActivatedLicenseEntitlementKind.Supporter)
        {
            throw new InvalidOperationException("This key belongs to a commercial license, not a supporter tier.");
        }
    }

    private void MarkCommercialActivationExpired()
    {
        CommercialStatus = LicenseStatus.Expired;
        CommercialTier = null;
        CommercialIsLifetime = false;
        _commercialLastValidated = DateTime.UtcNow;
    }

    private void MarkSupporterActivationExpired()
    {
        SupporterStatus = LicenseStatus.Expired;
        SupporterTier = null;
        _supporterLastValidated = DateTime.UtcNow;
    }

    private async Task<PolarActivationResponse> ActivateCoreAsync(string key, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, label = Environment.MachineName };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/activate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw CreatePolarException(json, $"Activation failed (HTTP {(int)response.StatusCode})", (int)response.StatusCode);

        return JsonSerializer.Deserialize<PolarActivationResponse>(json)
            ?? throw new InvalidOperationException("Activation failed: Polar returned an empty response.");
    }

    private async Task<PolarValidationResponse> ValidateCoreAsync(string key, string activationId, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, activation_id = activationId };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/validate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw CreatePolarException(json, $"Validation failed (HTTP {(int)response.StatusCode})", (int)response.StatusCode);

        return JsonSerializer.Deserialize<PolarValidationResponse>(json)
            ?? throw new InvalidOperationException("Validation failed: Polar returned an empty response.");
    }

    private async Task DeactivateCoreAsync(string key, string activationId, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, activation_id = activationId };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/deactivate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw CreatePolarException(json, $"Deactivation failed (HTTP {(int)response.StatusCode})", (int)response.StatusCode);
    }

    private async Task TryDeactivateCoreAsync(string key, string activationId, CancellationToken ct)
    {
        try
        {
            await DeactivateCoreAsync(key, activationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsLicenseOperationException(ex))
        {
            Debug.WriteLine($"Best-effort license deactivation failed: {ex.Message}");
        }
    }

    internal static CommercialLicenseTier? DetectCommercialTier(string? benefitId, string? benefitDescription)
    {
        var normalizedBenefitId = NormalizeBenefitIdentifier(benefitId);
        if (normalizedBenefitId is not null && KnownCommercialBenefitIds.TryGetValue(normalizedBenefitId, out var tier))
            return tier;

        var description = JoinBenefitText(benefitId, benefitDescription);
        if (description.Contains("enterprise") || description.Contains("unlimited device"))
            return CommercialLicenseTier.Enterprise;
        if (description.Contains("team") || description.Contains("10 device") || description.Contains("small teams"))
            return CommercialLicenseTier.Team;
        if (description.Contains("individual") ||
            description.Contains("single-seat") ||
            description.Contains("single seat") ||
            description.Contains("freelancer") ||
            description.Contains("2 device"))
        {
            return CommercialLicenseTier.Individual;
        }

        return null;
    }

    internal static SupporterTier? DetectSupporterTier(string? benefitId, string? benefitDescription)
    {
        var normalizedBenefitId = NormalizeBenefitIdentifier(benefitId);
        if (normalizedBenefitId is not null && KnownSupporterBenefitIds.TryGetValue(normalizedBenefitId, out var tier))
            return tier;

        var description = JoinBenefitText(benefitId, benefitDescription);
        if (!description.Contains("supporter") &&
            !description.Contains("bronze") &&
            !description.Contains("silver") &&
            !description.Contains("gold"))
        {
            return null;
        }

        if (description.Contains("gold")) return global::TypeWhisper.Windows.Services.SupporterTier.Gold;
        if (description.Contains("silver")) return global::TypeWhisper.Windows.Services.SupporterTier.Silver;
        if (description.Contains("bronze")) return global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
        return global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
    }

    private static string? NormalizeBenefitIdentifier(string? benefitId)
    {
        var normalized = benefitId?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string JoinBenefitText(params string?[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();

    private static bool IsLicenseOperationException(Exception ex) =>
        ex is HttpRequestException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or OperationCanceledException;

    private static string ResolveDataFilePath(string dataPath, string fileName)
    {
        var root = Path.GetFullPath(dataPath);
        var path = Path.GetFullPath(fileName, root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." ||
            (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)))
        {
            return path;
        }

        throw new InvalidOperationException("License data path must stay inside the configured data directory.");
    }

    private static PolarApiException CreatePolarException(string? json, string fallback, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PolarApiException(fallback, statusCode);

        try
        {
            var error = JsonSerializer.Deserialize<PolarErrorResponse>(json);
            if (!string.IsNullOrWhiteSpace(error?.Detail))
                return new PolarApiException(error.Detail, statusCode, error.Detail, error.Type);

            if (!string.IsNullOrWhiteSpace(error?.Type))
                return new PolarApiException(error.Type, statusCode, null, error.Type);
        }
        catch
        {
            // Ignore malformed responses and fall back.
        }

        return new PolarApiException(fallback, statusCode);
    }

    private static bool IsPolarResourceMissing(Exception ex)
    {
        if (ex is PolarApiException { StatusCode: 404 })
            return true;

        if (ex is PolarApiException polar &&
            (ContainsResourceMissingSignal(polar.Detail) || ContainsResourceMissingSignal(polar.Type)))
        {
            return true;
        }

        return ContainsResourceMissingSignal(ex.Message);
    }

    private static bool ContainsResourceMissingSignal(string? value) =>
        value?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("resource not found", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("resourcenotfound", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("no licensekeyactivation", StringComparison.OrdinalIgnoreCase) == true;

    private void ResetCommercialState(bool clearSecrets)
    {
        CommercialStatus = LicenseStatus.Unlicensed;
        CommercialTier = null;
        CommercialIsLifetime = false;
        CommercialActivationError = null;
        CommercialDeactivationError = null;
        CommercialRefreshError = null;
        _commercialLastValidated = null;

        if (clearSecrets)
        {
            _commercialLicenseKey = null;
            _commercialActivationId = null;
        }
    }

    private void ResetSupporterState(bool clearSecrets)
    {
        SupporterStatus = LicenseStatus.Unlicensed;
        SupporterTier = null;
        SupporterActivationError = null;
        SupporterDeactivationError = null;
        SupporterRefreshError = null;
        _supporterLastValidated = null;

        if (clearSecrets)
        {
            _supporterLicenseKey = null;
            _supporterActivationId = null;
        }
    }

    private void PersistStore()
    {
        if (_suppressPersistence)
            return;

        try
        {
            var data = new LicenseStoreData
            {
                UserType = UserType.ToString(),
                Commercial = BuildStoredCredential(
                    _commercialLicenseKey,
                    _commercialActivationId,
                    CommercialStatus,
                    CommercialTier?.ToString(),
                    CommercialIsLifetime,
                    _commercialLastValidated),
                Supporter = BuildStoredCredential(
                    _supporterLicenseKey,
                    _supporterActivationId,
                    SupporterStatus,
                    SupporterTier?.ToString(),
                    false,
                    _supporterLastValidated),
            };

            var json = JsonSerializer.Serialize(data);
            var protectedPayload = Protect(json);
            Directory.CreateDirectory(Path.GetDirectoryName(_credentialPath)!);
            File.WriteAllText(_credentialPath, protectedPayload, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Persisting license store failed: {ex.Message}");
        }
    }

    private static StoredCredential? BuildStoredCredential(
        string? key,
        string? activationId,
        LicenseStatus status,
        string? tier,
        bool isLifetime,
        DateTime? lastValidated)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(activationId))
            return null;

        return new StoredCredential
        {
            Key = key,
            ActivationId = activationId,
            Status = status.ToString(),
            Tier = tier,
            IsLifetime = isLifetime,
            LastValidated = lastValidated?.ToString("o"),
        };
    }

    private void LoadStore()
    {
        _suppressPersistence = true;

        try
        {
            if (TryLoadEncryptedStore())
                return;

            TryMigrateLegacyStore();
        }
        finally
        {
            _suppressPersistence = false;
        }
    }

    private bool TryLoadEncryptedStore()
    {
        if (!File.Exists(_credentialPath))
            return false;

        try
        {
            var raw = File.ReadAllText(_credentialPath, Encoding.UTF8);
            var json = Unprotect(raw);
            var data = JsonSerializer.Deserialize<LicenseStoreData>(json);
            if (data is null)
                return false;

            ApplyStore(data);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading encrypted license store failed: {ex.Message}");
            return false;
        }
    }

    private void TryMigrateLegacyStore()
    {
        if (!File.Exists(_legacyCredentialPath))
            return;

        try
        {
            var json = File.ReadAllText(_legacyCredentialPath, Encoding.UTF8);
            var legacy = JsonSerializer.Deserialize<LegacyLicenseData>(json);
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.Key) || string.IsNullOrWhiteSpace(legacy.ActivationId))
                return;

            _supporterLicenseKey = legacy.Key;
            _supporterActivationId = legacy.ActivationId;
            SupporterStatus = Enum.TryParse<LicenseStatus>(legacy.Status, out var status)
                ? status
                : LicenseStatus.Unlicensed;
            SupporterTier = Enum.TryParse<SupporterTier>(legacy.Tier, out var tier)
                ? NormalizePersistedSupporterTier(tier, SupporterStatus)
                : null;
            _supporterLastValidated = DateTime.TryParse(legacy.LastValidated, out var lastValidated)
                ? lastValidated
                : null;

            PersistStore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Migrating legacy license store failed: {ex.Message}");
        }
    }

    private void ApplyStore(LicenseStoreData data)
    {
        _userType = Enum.TryParse<LicenseUserType>(data.UserType, out var userType)
            ? userType
            : LicenseUserType.PrivateUser;

        if (data.Commercial is { } commercial)
        {
            _commercialLicenseKey = commercial.Key;
            _commercialActivationId = commercial.ActivationId;
            _commercialStatus = Enum.TryParse<LicenseStatus>(commercial.Status, out var commercialStatus)
                ? commercialStatus
                : LicenseStatus.Unlicensed;
            _commercialTier = Enum.TryParse<CommercialLicenseTier>(commercial.Tier, out var commercialTier)
                ? commercialTier
                : null;
            _commercialIsLifetime = commercial.IsLifetime;
            _commercialLastValidated = DateTime.TryParse(commercial.LastValidated, out var commercialLastValidated)
                ? commercialLastValidated
                : null;
        }

        if (data.Supporter is { } supporter)
        {
            _supporterLicenseKey = supporter.Key;
            _supporterActivationId = supporter.ActivationId;
            _supporterStatus = Enum.TryParse<LicenseStatus>(supporter.Status, out var supporterStatus)
                ? supporterStatus
                : LicenseStatus.Unlicensed;
            _supporterTier = Enum.TryParse<SupporterTier>(supporter.Tier, out var supporterTier)
                ? NormalizePersistedSupporterTier(supporterTier, _supporterStatus)
                : null;
            _supporterLastValidated = DateTime.TryParse(supporter.LastValidated, out var supporterLastValidated)
                ? supporterLastValidated
                : null;
        }
    }

    private static string Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string encrypted)
    {
        var bytes = Convert.FromBase64String(encrypted);
        var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    private static SupporterTier? NormalizePersistedSupporterTier(SupporterTier tier, LicenseStatus status) =>
        tier == global::TypeWhisper.Windows.Services.SupporterTier.None && status == LicenseStatus.Active
            ? global::TypeWhisper.Windows.Services.SupporterTier.Bronze
            : tier;

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasCommercialLicense));
        OnPropertyChanged(nameof(HasSupporterLicense));
        OnPropertyChanged(nameof(HasCommercialActivation));
        OnPropertyChanged(nameof(HasSupporterActivation));
        OnPropertyChanged(nameof(IsSupporter));
        OnPropertyChanged(nameof(SupporterBadgeTier));
        OnPropertyChanged(nameof(IsPrivateUser));
        OnPropertyChanged(nameof(IsBusinessUser));
        OnPropertyChanged(nameof(ShouldShowReminder));
        OnPropertyChanged(nameof(CommercialTierDisplayName));
        OnPropertyChanged(nameof(SupporterTierDisplayName));
        StatusChanged?.Invoke();
    }

    partial void OnCommercialStatusChanged(LicenseStatus value)
    {
        if (!_suppressPersistence)
            NotifyStateChanged();
    }

    partial void OnSupporterStatusChanged(LicenseStatus value)
    {
        if (!_suppressPersistence)
            NotifyStateChanged();
    }

    partial void OnCommercialTierChanged(CommercialLicenseTier? value)
    {
        if (!_suppressPersistence)
            NotifyStateChanged();
    }

    partial void OnSupporterTierChanged(SupporterTier? value)
    {
        if (!_suppressPersistence)
            NotifyStateChanged();
    }

    private sealed record LicenseStoreData
    {
        [JsonPropertyName("userType")] public string? UserType { get; init; }
        [JsonPropertyName("commercial")] public StoredCredential? Commercial { get; init; }
        [JsonPropertyName("supporter")] public StoredCredential? Supporter { get; init; }
    }

    private sealed record StoredCredential
    {
        [JsonPropertyName("key")] public string? Key { get; init; }
        [JsonPropertyName("activationId")] public string? ActivationId { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("tier")] public string? Tier { get; init; }
        [JsonPropertyName("isLifetime")] public bool IsLifetime { get; init; }
        [JsonPropertyName("lastValidated")] public string? LastValidated { get; init; }
    }

    private sealed record LegacyLicenseData
    {
        [JsonPropertyName("key")] public string? Key { get; init; }
        [JsonPropertyName("activationId")] public string? ActivationId { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("tier")] public string? Tier { get; init; }
        [JsonPropertyName("isLifetime")] public bool IsLifetime { get; init; }
        [JsonPropertyName("lastValidated")] public string? LastValidated { get; init; }
    }

    private sealed record PolarActivationResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
    }

    private sealed record PolarValidationResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; init; }
        [JsonPropertyName("benefit_id")] public string? BenefitId { get; init; }
        [JsonPropertyName("benefit")] public PolarBenefit? Benefit { get; init; }

        public string? ResolvedBenefitId => Benefit?.Id ?? BenefitId;
        public string? ResolvedBenefitDescription => Benefit?.Description;
    }

    private sealed record PolarBenefit
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    private sealed record PolarErrorResponse
    {
        [JsonPropertyName("detail")] public string? Detail { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
    }

    private enum ExpectedLicenseEntitlementKind
    {
        Any,
        Commercial,
        Supporter,
    }

    private sealed class PolarApiException : InvalidOperationException
    {
        public PolarApiException(string message, int statusCode, string? detail = null, string? type = null)
            : base(message)
        {
            StatusCode = statusCode;
            Detail = detail;
            Type = type;
        }

        public int StatusCode { get; }
        public string? Detail { get; }
        public string? Type { get; }
    }
}

public enum LicenseUserType
{
    PrivateUser,
    Business,
}

public enum LicenseStatus
{
    Unlicensed,
    Active,
    Expired,
}

public enum CommercialLicenseTier
{
    Individual,
    Team,
    Enterprise,
}

public enum SupporterTier
{
    None,
    Bronze,
    Silver,
    Gold,
}

public enum ActivatedLicenseEntitlementKind
{
    Commercial,
    Supporter,
}

public sealed record ActivatedLicenseEntitlement(
    ActivatedLicenseEntitlementKind Kind,
    CommercialLicenseTier? CommercialTier = null,
    SupporterTier? SupporterTier = null,
    bool IsLifetime = false)
{
    public static ActivatedLicenseEntitlement Commercial(CommercialLicenseTier tier, bool isLifetime) =>
        new(ActivatedLicenseEntitlementKind.Commercial, tier, null, isLifetime);

    public static ActivatedLicenseEntitlement Supporter(SupporterTier tier) =>
        new(ActivatedLicenseEntitlementKind.Supporter, null, tier);
}

public sealed record SupporterClaimProof(string Key, string ActivationId, SupporterTier Tier);
