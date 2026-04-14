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
    private static readonly byte[] Entropy = "TypeWhisper.License.v2"u8.ToArray();
    private static readonly TimeSpan CommercialValidationInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan SupporterValidationInterval = TimeSpan.FromDays(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
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
    private bool _isCommercialActivating;

    [ObservableProperty]
    private string? _commercialActivationError;

    [ObservableProperty]
    private string? _commercialDeactivationError;

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

    public event Action? StatusChanged;

    public LicenseService()
    {
        _credentialPath = Path.Combine(TypeWhisperEnvironment.DataPath, "licenses.dat");
        _legacyCredentialPath = Path.Combine(TypeWhisperEnvironment.DataPath, "license.json");
        LoadStore();
    }

    public bool IsPrivateUser => UserType == LicenseUserType.PrivateUser;
    public bool IsBusinessUser => UserType == LicenseUserType.Business;
    public bool HasCommercialLicense => CommercialStatus == LicenseStatus.Active;
    public bool HasSupporterLicense => SupporterStatus == LicenseStatus.Active;
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

    public async Task ActivateCommercialLicenseAsync(string key, CancellationToken ct = default)
    {
        IsCommercialActivating = true;
        CommercialActivationError = null;
        CommercialDeactivationError = null;

        try
        {
            var activation = await ActivateCoreAsync(key, ct);
            _commercialLicenseKey = key;
            _commercialActivationId = activation.Id ?? throw new InvalidOperationException("Activation failed: Polar did not return an activation id.");

            CommercialStatus = LicenseStatus.Active;
            CommercialTier = null;
            CommercialIsLifetime = false;

            try
            {
                var validation = await ValidateCoreAsync(key, _commercialActivationId, ct);
                CommercialStatus = validation.Status == "granted" ? LicenseStatus.Active : LicenseStatus.Expired;
                CommercialTier = DetectCommercialTier(validation.Benefit?.Description);
                CommercialIsLifetime = validation.ExpiresAt is null;
                _commercialLastValidated = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Commercial validation after activation failed: {ex.Message}");
                _commercialLastValidated = DateTime.UtcNow;
            }

            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            CommercialActivationError = ex.Message;
        }
        finally
        {
            IsCommercialActivating = false;
        }
    }

    public async Task ValidateCommercialLicenseAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_commercialLicenseKey) || string.IsNullOrWhiteSpace(_commercialActivationId))
            return;

        try
        {
            var validation = await ValidateCoreAsync(_commercialLicenseKey, _commercialActivationId, ct);
            CommercialStatus = validation.Status == "granted" ? LicenseStatus.Active : LicenseStatus.Expired;
            CommercialTier = DetectCommercialTier(validation.Benefit?.Description);
            CommercialIsLifetime = validation.ExpiresAt is null;
            _commercialLastValidated = DateTime.UtcNow;
            CommercialActivationError = null;
            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Commercial license validation failed: {ex.Message}");
        }
    }

    public async Task ValidateCommercialIfNeededAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_commercialLicenseKey) || string.IsNullOrWhiteSpace(_commercialActivationId))
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
        if (string.IsNullOrWhiteSpace(_commercialLicenseKey) || string.IsNullOrWhiteSpace(_commercialActivationId))
            return;

        CommercialDeactivationError = null;

        try
        {
            await DeactivateCoreAsync(_commercialLicenseKey, _commercialActivationId, ct);
            ResetCommercialState(clearSecrets: true);
            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            CommercialDeactivationError = ex.Message;
        }
    }

    public async Task ActivateSupporterKeyAsync(string key, CancellationToken ct = default)
    {
        IsSupporterActivating = true;
        SupporterActivationError = null;
        SupporterDeactivationError = null;

        try
        {
            var activation = await ActivateCoreAsync(key, ct);
            _supporterLicenseKey = key;
            _supporterActivationId = activation.Id ?? throw new InvalidOperationException("Activation failed: Polar did not return an activation id.");

            SupporterStatus = LicenseStatus.Active;
            SupporterTier = null;

            try
            {
                var validation = await ValidateCoreAsync(key, _supporterActivationId, ct);
                SupporterStatus = validation.Status == "granted" ? LicenseStatus.Active : LicenseStatus.Expired;
                SupporterTier = DetectSupporterTier(validation.Benefit?.Description) ?? global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
                _supporterLastValidated = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Supporter validation after activation failed: {ex.Message}");
                SupporterTier = global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
                _supporterLastValidated = DateTime.UtcNow;
            }

            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            SupporterActivationError = ex.Message;
        }
        finally
        {
            IsSupporterActivating = false;
        }
    }

    public async Task ValidateSupporterAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_supporterLicenseKey) || string.IsNullOrWhiteSpace(_supporterActivationId))
            return;

        try
        {
            var validation = await ValidateCoreAsync(_supporterLicenseKey, _supporterActivationId, ct);
            SupporterStatus = validation.Status == "granted" ? LicenseStatus.Active : LicenseStatus.Expired;
            SupporterTier = validation.Status == "granted"
                ? DetectSupporterTier(validation.Benefit?.Description) ?? global::TypeWhisper.Windows.Services.SupporterTier.Bronze
                : null;
            _supporterLastValidated = DateTime.UtcNow;
            SupporterActivationError = null;
            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Supporter validation failed: {ex.Message}");
        }
    }

    public async Task ValidateSupporterIfNeededAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_supporterLicenseKey) || string.IsNullOrWhiteSpace(_supporterActivationId))
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
        if (string.IsNullOrWhiteSpace(_supporterLicenseKey) || string.IsNullOrWhiteSpace(_supporterActivationId))
            return;

        SupporterDeactivationError = null;

        try
        {
            await DeactivateCoreAsync(_supporterLicenseKey, _supporterActivationId, ct);
            ResetSupporterState(clearSecrets: true);
            PersistStore();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            SupporterDeactivationError = ex.Message;
        }
    }

    public async Task ValidateAllIfNeededAsync(CancellationToken ct = default)
    {
        await ValidateCommercialIfNeededAsync(ct);
        await ValidateSupporterIfNeededAsync(ct);
    }

    private async Task<PolarActivationResponse> ActivateCoreAsync(string key, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, label = Environment.MachineName };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/activate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParsePolarError(json, $"Activation failed (HTTP {(int)response.StatusCode})"));

        return JsonSerializer.Deserialize<PolarActivationResponse>(json)
            ?? throw new InvalidOperationException("Activation failed: Polar returned an empty response.");
    }

    private async Task<PolarValidationResponse> ValidateCoreAsync(string key, string activationId, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, activation_id = activationId };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/validate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParsePolarError(json, $"Validation failed (HTTP {(int)response.StatusCode})"));

        return JsonSerializer.Deserialize<PolarValidationResponse>(json)
            ?? throw new InvalidOperationException("Validation failed: Polar returned an empty response.");
    }

    private async Task DeactivateCoreAsync(string key, string activationId, CancellationToken ct)
    {
        var body = new { key, organization_id = OrganizationId, activation_id = activationId };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/deactivate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParsePolarError(json, $"Deactivation failed (HTTP {(int)response.StatusCode})"));
    }

    private static CommercialLicenseTier? DetectCommercialTier(string? benefitDescription)
    {
        var description = benefitDescription?.ToLowerInvariant() ?? "";
        if (description.Contains("enterprise")) return CommercialLicenseTier.Enterprise;
        if (description.Contains("team")) return CommercialLicenseTier.Team;
        if (description.Contains("individual")) return CommercialLicenseTier.Individual;
        return null;
    }

    private static SupporterTier? DetectSupporterTier(string? benefitDescription)
    {
        var description = benefitDescription?.ToLowerInvariant() ?? "";
        if (description.Contains("gold")) return global::TypeWhisper.Windows.Services.SupporterTier.Gold;
        if (description.Contains("silver")) return global::TypeWhisper.Windows.Services.SupporterTier.Silver;
        if (description.Contains("bronze")) return global::TypeWhisper.Windows.Services.SupporterTier.Bronze;
        return null;
    }

    private static string ParsePolarError(string? json, string fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            var error = JsonSerializer.Deserialize<PolarErrorResponse>(json);
            if (!string.IsNullOrWhiteSpace(error?.Detail))
                return $"Activation failed: {error.Detail}";
        }
        catch
        {
            // Ignore malformed responses and fall back.
        }

        return fallback;
    }

    private void ResetCommercialState(bool clearSecrets)
    {
        CommercialStatus = LicenseStatus.Unlicensed;
        CommercialTier = null;
        CommercialIsLifetime = false;
        CommercialActivationError = null;
        CommercialDeactivationError = null;
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
        [JsonPropertyName("benefit")] public PolarBenefit? Benefit { get; init; }
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

public sealed record SupporterClaimProof(string Key, string ActivationId, SupporterTier Tier);
