using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Describes the product origin claimed by a registry artifact.
/// </summary>
public enum RegistryArtifactSource
{
    /// <summary>
    /// No supported registry origin was supplied.
    /// </summary>
    Unknown,
    /// <summary>
    /// The plugin is maintained by TypeWhisper.
    /// </summary>
    Official,
    /// <summary>
    /// The plugin is maintained by a community publisher.
    /// </summary>
    Community
}

/// <summary>
/// Describes the verified build provenance of a registry artifact.
/// </summary>
public enum RegistryArtifactTrustLevel
{
    /// <summary>
    /// The artifact has no valid TypeWhisper build attestation.
    /// </summary>
    Unverified,
    /// <summary>
    /// The artifact was produced by the TypeWhisper build and signing process.
    /// </summary>
    TypeWhisperBuilt
}

/// <summary>
/// Provides stable result codes for registry artifact validation.
/// </summary>
public enum RegistryArtifactValidationCode
{
    /// <summary>
    /// The complete artifact attestation is valid.
    /// </summary>
    Verified,
    /// <summary>
    /// One or more required attestation fields are missing.
    /// </summary>
    MissingMetadata,
    /// <summary>
    /// The source value is not supported.
    /// </summary>
    UnsupportedSource,
    /// <summary>
    /// The trust value is not supported.
    /// </summary>
    UnsupportedTrust,
    /// <summary>
    /// The source repository is invalid or violates source policy.
    /// </summary>
    InvalidSourceRepository,
    /// <summary>
    /// The source commit is not a full hexadecimal commit identifier.
    /// </summary>
    InvalidSourceCommit,
    /// <summary>
    /// The package URL is invalid or not HTTPS.
    /// </summary>
    InvalidDownloadUrl,
    /// <summary>
    /// The package SHA-256 is invalid.
    /// </summary>
    InvalidPackageHash,
    /// <summary>
    /// The signed package size is invalid.
    /// </summary>
    InvalidPackageSize,
    /// <summary>
    /// The plugin ID is not a safe canonical registry identifier.
    /// </summary>
    InvalidPluginId,
    /// <summary>
    /// The plugin version is not a supported version value.
    /// </summary>
    InvalidVersion,
    /// <summary>
    /// The attestation schema is not supported.
    /// </summary>
    UnsupportedSchema,
    /// <summary>
    /// The attestation algorithm is not supported.
    /// </summary>
    UnsupportedAlgorithm,
    /// <summary>
    /// The key identifier is malformed.
    /// </summary>
    InvalidKeyId,
    /// <summary>
    /// The attestation references a key that is not in the trusted keyring.
    /// </summary>
    UnknownKey,
    /// <summary>
    /// The attestation references a revoked key.
    /// </summary>
    RevokedKey,
    /// <summary>
    /// The configured public key is malformed or uses the wrong curve.
    /// </summary>
    InvalidPublicKey,
    /// <summary>
    /// The attestation signature is malformed or does not match the artifact metadata.
    /// </summary>
    InvalidSignature
}

/// <summary>
/// Contains the signed build provenance fields of a registry entry.
/// </summary>
public sealed record RegistryArtifactAttestation
{
    /// <summary>
    /// Gets the canonical attestation schema version.
    /// </summary>
    public int SchemaVersion { get; init; }
    /// <summary>
    /// Gets the signing algorithm identifier.
    /// </summary>
    public string? Algorithm { get; init; }
    /// <summary>
    /// Gets the trusted key identifier.
    /// </summary>
    public string? KeyId { get; init; }
    /// <summary>
    /// Gets the full source commit identifier used for the build.
    /// </summary>
    public string? SourceCommit { get; init; }
    /// <summary>
    /// Gets the base64-encoded IEEE P1363 signature.
    /// </summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Defines one public key in the registry artifact trust keyring.
/// </summary>
/// <param name="KeyId">The stable key identifier.</param>
/// <param name="Algorithm">The signing algorithm identifier.</param>
/// <param name="SubjectPublicKeyInfo">The base64-encoded SubjectPublicKeyInfo bytes.</param>
/// <param name="IsRevoked">Whether the key must no longer be accepted.</param>
public sealed record RegistryArtifactTrustKey(
    string KeyId,
    string Algorithm,
    string SubjectPublicKeyInfo,
    bool IsRevoked = false);

/// <summary>
/// Describes the result of validating one registry artifact attestation.
/// </summary>
/// <param name="Code">The stable validation result code.</param>
/// <param name="Source">The parsed source classification.</param>
/// <param name="TrustLevel">The effective verified trust level.</param>
public sealed record RegistryArtifactValidationResult(
    RegistryArtifactValidationCode Code,
    RegistryArtifactSource Source,
    RegistryArtifactTrustLevel TrustLevel)
{
    /// <summary>
    /// Gets whether the registry artifact has a valid TypeWhisper build attestation.
    /// </summary>
    public bool IsVerified => Code == RegistryArtifactValidationCode.Verified;
}

/// <summary>
/// Thrown when registry artifact trust policy blocks an install or update.
/// </summary>
public sealed class RegistryArtifactTrustException : InvalidOperationException
{
    /// <summary>
    /// Gets the stable validation result code.
    /// </summary>
    public RegistryArtifactValidationCode ValidationCode { get; }

    /// <summary>
    /// Initializes a new instance of the RegistryArtifactTrustException class.
    /// </summary>
    public RegistryArtifactTrustException(RegistryArtifactValidationCode validationCode, string message)
        : base(message)
    {
        ValidationCode = validationCode;
    }
}

/// <summary>
/// Validates signed registry metadata independently from display metadata.
/// </summary>
public sealed class RegistryArtifactTrustValidator
{
    /// <summary>
    /// The supported canonical attestation schema.
    /// </summary>
    public const int SupportedSchemaVersion = 1;
    /// <summary>
    /// The supported ECDSA algorithm identifier.
    /// </summary>
    public const string SupportedAlgorithm = "ecdsa-p256-sha256";
    /// <summary>
    /// The trust value used for artifacts built by TypeWhisper.
    /// </summary>
    public const string TypeWhisperBuiltTrust = "typewhisper-built";

    private static readonly RegistryArtifactValidationResult MissingMetadataResult = new(
        RegistryArtifactValidationCode.MissingMetadata,
        RegistryArtifactSource.Unknown,
        RegistryArtifactTrustLevel.Unverified);

    private readonly IReadOnlyDictionary<string, RegistryArtifactTrustKey> _keys;

    /// <summary>
    /// Gets a validator with no production keys configured.
    /// </summary>
    public static RegistryArtifactTrustValidator Empty { get; } = new([]);

    /// <summary>
    /// Initializes a new instance of the RegistryArtifactTrustValidator class.
    /// </summary>
    public RegistryArtifactTrustValidator(IEnumerable<RegistryArtifactTrustKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var keyring = new Dictionary<string, RegistryArtifactTrustKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (string.IsNullOrWhiteSpace(key.KeyId))
                throw new ArgumentException("Registry artifact key IDs cannot be empty.", nameof(keys));
            if (!keyring.TryAdd(key.KeyId, key))
                throw new ArgumentException($"Duplicate registry artifact key ID '{key.KeyId}'.", nameof(keys));
        }

        _keys = keyring;
    }

    /// <summary>
    /// Gets whether an entry makes any registry trust claim.
    /// </summary>
    public static bool HasAnyTrustMetadata(RegistryPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return !string.IsNullOrWhiteSpace(plugin.Source) ||
               !string.IsNullOrWhiteSpace(plugin.Trust) ||
               !string.IsNullOrWhiteSpace(plugin.SourceRepository) ||
               plugin.Attestation is not null;
    }

    /// <summary>
    /// Creates the canonical UTF-8 payload covered by the artifact signature.
    /// </summary>
    public static byte[] CreateCanonicalPayload(RegistryPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var normalized = NormalizeSignedMetadata(plugin);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", normalized.SchemaVersion);
            writer.WriteString("algorithm", normalized.Algorithm);
            writer.WriteString("keyId", normalized.KeyId);
            writer.WriteString("id", normalized.PluginId);
            writer.WriteString("version", normalized.Version);
            writer.WriteString("source", normalized.SourceName);
            writer.WriteString("trust", normalized.TrustName);
            writer.WriteString("sourceRepository", normalized.SourceRepository);
            writer.WriteString("sourceCommit", normalized.SourceCommit);
            writer.WriteString("downloadUrl", normalized.DownloadUrl);
            writer.WriteString("sha256", normalized.Sha256);
            writer.WriteNumber("size", normalized.Size);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Validates an entry against source policy and the configured keyring.
    /// </summary>
    public RegistryArtifactValidationResult Validate(RegistryPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (!HasCompleteTrustMetadata(plugin))
            return MissingMetadataResult;

        NormalizedRegistryArtifact normalized;
        try
        {
            normalized = NormalizeSignedMetadata(plugin);
        }
        catch (RegistryArtifactTrustException ex)
        {
            return Failure(ex.ValidationCode, ParseSource(plugin.Source));
        }

        if (!_keys.TryGetValue(normalized.KeyId, out var key))
            return Failure(RegistryArtifactValidationCode.UnknownKey, normalized.Source);
        if (key.IsRevoked)
            return Failure(RegistryArtifactValidationCode.RevokedKey, normalized.Source);
        if (!string.Equals(key.Algorithm, SupportedAlgorithm, StringComparison.Ordinal))
            return Failure(RegistryArtifactValidationCode.UnsupportedAlgorithm, normalized.Source);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(plugin.Attestation!.Signature!);
        }
        catch (FormatException)
        {
            return Failure(RegistryArtifactValidationCode.InvalidSignature, normalized.Source);
        }

        if (signature.Length != 64)
            return Failure(RegistryArtifactValidationCode.InvalidSignature, normalized.Source);

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(key.SubjectPublicKeyInfo);
        }
        catch (FormatException)
        {
            return Failure(RegistryArtifactValidationCode.InvalidPublicKey, normalized.Source);
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256)
                return Failure(RegistryArtifactValidationCode.InvalidPublicKey, normalized.Source);

            var payload = CreateCanonicalPayload(plugin);
            return ecdsa.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? new RegistryArtifactValidationResult(
                    RegistryArtifactValidationCode.Verified,
                    normalized.Source,
                    RegistryArtifactTrustLevel.TypeWhisperBuilt)
                : Failure(RegistryArtifactValidationCode.InvalidSignature, normalized.Source);
        }
        catch (CryptographicException)
        {
            return Failure(RegistryArtifactValidationCode.InvalidPublicKey, normalized.Source);
        }
    }

    private static bool HasCompleteTrustMetadata(RegistryPlugin plugin) =>
        !string.IsNullOrWhiteSpace(plugin.Id) &&
        !string.IsNullOrWhiteSpace(plugin.Version) &&
        !string.IsNullOrWhiteSpace(plugin.Source) &&
        !string.IsNullOrWhiteSpace(plugin.Trust) &&
        !string.IsNullOrWhiteSpace(plugin.SourceRepository) &&
        !string.IsNullOrWhiteSpace(plugin.DownloadUrl) &&
        !string.IsNullOrWhiteSpace(plugin.Sha256) &&
        plugin.Attestation is not null &&
        !string.IsNullOrWhiteSpace(plugin.Attestation.Algorithm) &&
        !string.IsNullOrWhiteSpace(plugin.Attestation.KeyId) &&
        !string.IsNullOrWhiteSpace(plugin.Attestation.SourceCommit) &&
        !string.IsNullOrWhiteSpace(plugin.Attestation.Signature);

    private static NormalizedRegistryArtifact NormalizeSignedMetadata(RegistryPlugin plugin)
    {
        if (!HasCompleteTrustMetadata(plugin))
            throw TrustFailure(RegistryArtifactValidationCode.MissingMetadata);

        var source = ParseSource(plugin.Source);
        if (source == RegistryArtifactSource.Unknown)
            throw TrustFailure(RegistryArtifactValidationCode.UnsupportedSource);
        if (!string.Equals(plugin.Trust!.Trim(), TypeWhisperBuiltTrust, StringComparison.OrdinalIgnoreCase))
            throw TrustFailure(RegistryArtifactValidationCode.UnsupportedTrust);

        var attestation = plugin.Attestation!;
        if (attestation.SchemaVersion != SupportedSchemaVersion)
            throw TrustFailure(RegistryArtifactValidationCode.UnsupportedSchema);
        if (!string.Equals(attestation.Algorithm!.Trim(), SupportedAlgorithm, StringComparison.Ordinal))
            throw TrustFailure(RegistryArtifactValidationCode.UnsupportedAlgorithm);

        var pluginId = plugin.Id.Trim().ToLowerInvariant();
        if (!IsValidPluginId(pluginId))
            throw TrustFailure(RegistryArtifactValidationCode.InvalidPluginId);
        var version = plugin.Version.Trim();
        if (!Version.TryParse(version, out _))
            throw TrustFailure(RegistryArtifactValidationCode.InvalidVersion);
        var keyId = attestation.KeyId!.Trim();
        if (!IsValidKeyId(keyId))
            throw TrustFailure(RegistryArtifactValidationCode.InvalidKeyId);

        var sourceRepository = NormalizeRepository(plugin.SourceRepository!, source);
        var downloadUrl = NormalizeDownloadUrl(plugin.DownloadUrl);
        var sourceCommit = NormalizeSourceCommit(attestation.SourceCommit!);
        var sha256 = NormalizeSha256(plugin.Sha256!);
        if (plugin.Size <= 0)
            throw TrustFailure(RegistryArtifactValidationCode.InvalidPackageSize);

        return new NormalizedRegistryArtifact(
            attestation.SchemaVersion,
            SupportedAlgorithm,
            keyId,
            pluginId,
            version,
            source,
            source == RegistryArtifactSource.Official ? "official" : "community",
            TypeWhisperBuiltTrust,
            sourceRepository,
            sourceCommit,
            downloadUrl,
            sha256,
            plugin.Size);
    }

    private static RegistryArtifactSource ParseSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "official" => RegistryArtifactSource.Official,
        "community" => RegistryArtifactSource.Community,
        _ => RegistryArtifactSource.Unknown
    };

    private static string NormalizeRepository(string repository, RegistryArtifactSource source)
    {
        if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw TrustFailure(RegistryArtifactValidationCode.InvalidSourceRepository);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            (source == RegistryArtifactSource.Official &&
             !string.Equals(segments[0], "TypeWhisper", StringComparison.OrdinalIgnoreCase)))
        {
            throw TrustFailure(RegistryArtifactValidationCode.InvalidSourceRepository);
        }

        return $"https://github.com/{segments[0]}/{segments[1]}";
    }

    private static string NormalizeDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !(string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw TrustFailure(RegistryArtifactValidationCode.InvalidDownloadUrl);
        }

        return uri.AbsoluteUri;
    }

    private static string NormalizeSourceCommit(string sourceCommit)
    {
        var normalized = sourceCommit.Trim().ToLowerInvariant();
        if ((normalized.Length is not 40 and not 64) || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw TrustFailure(RegistryArtifactValidationCode.InvalidSourceCommit);
        return normalized;
    }

    private static string NormalizeSha256(string sha256)
    {
        var normalized = sha256.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw TrustFailure(RegistryArtifactValidationCode.InvalidPackageHash);
        return normalized;
    }

    private static bool IsValidPluginId(string pluginId) =>
        pluginId.Length is > 0 and <= 128 &&
        char.IsLetterOrDigit(pluginId[0]) &&
        char.IsLetterOrDigit(pluginId[^1]) &&
        !pluginId.Contains("..", StringComparison.Ordinal) &&
        pluginId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsValidKeyId(string keyId) =>
        keyId.Length is > 0 and <= 64 &&
        char.IsAsciiLetterOrDigit(keyId[0]) &&
        keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static RegistryArtifactValidationResult Failure(
        RegistryArtifactValidationCode code,
        RegistryArtifactSource source) =>
        new(code, source, RegistryArtifactTrustLevel.Unverified);

    private static RegistryArtifactTrustException TrustFailure(RegistryArtifactValidationCode code) =>
        new(code, $"Registry artifact validation failed: {code}.");

    private sealed record NormalizedRegistryArtifact(
        int SchemaVersion,
        string Algorithm,
        string KeyId,
        string PluginId,
        string Version,
        RegistryArtifactSource Source,
        string SourceName,
        string TrustName,
        string SourceRepository,
        string SourceCommit,
        string DownloadUrl,
        string Sha256,
        long Size);
}
