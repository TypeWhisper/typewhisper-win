using System.Security.Cryptography;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class RegistryArtifactTrustValidatorTests
{
    [Fact]
    public void Validate_AcceptsOfficialArtifactSignedByActiveKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(key, source: "official");
        var validator = CreateValidator(key);

        var result = validator.Validate(plugin);

        Assert.True(result.IsVerified);
        Assert.Equal(RegistryArtifactSource.Official, result.Source);
        Assert.Equal(RegistryArtifactTrustLevel.TypeWhisperBuilt, result.TrustLevel);
    }

    [Fact]
    public void Validate_AcceptsCommunityArtifactWithoutChangingItsSourceLabel()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(
            key,
            source: "community",
            sourceRepository: "https://github.com/community/example-plugin");
        var validator = CreateValidator(key);

        var result = validator.Validate(plugin);

        Assert.True(result.IsVerified);
        Assert.Equal(RegistryArtifactSource.Community, result.Source);
        Assert.Equal(RegistryArtifactTrustLevel.TypeWhisperBuilt, result.TrustLevel);
    }

    [Fact]
    public void Validate_AcceptsBothKeysDuringRotation()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validator = new RegistryArtifactTrustValidator([
            ExportTrustKey(oldKey, "old-key"),
            ExportTrustKey(newKey, "new-key")
        ]);

        var oldResult = validator.Validate(CreateSignedPlugin(oldKey, keyId: "old-key"));
        var newResult = validator.Validate(CreateSignedPlugin(newKey, keyId: "new-key"));

        Assert.True(oldResult.IsVerified);
        Assert.True(newResult.IsVerified);
    }

    [Fact]
    public void Validate_RejectsRevokedKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(key);
        var validator = new RegistryArtifactTrustValidator([
            ExportTrustKey(key, "test-key") with { IsRevoked = true }
        ]);

        var result = validator.Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.RevokedKey, result.Code);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void Validate_RejectsUnknownKey()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var configuredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(signingKey, keyId: "unconfigured-key");
        var validator = CreateValidator(configuredKey);

        var result = validator.Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.UnknownKey, result.Code);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void Validate_OfficialClaimWithoutAttestationIsUnverified()
    {
        var plugin = CreateUnsignedPlugin() with
        {
            Source = "official"
        };

        var result = RegistryArtifactTrustValidator.Empty.Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.MissingMetadata, result.Code);
        Assert.Equal(RegistryArtifactSource.Official, result.Source);
        Assert.Equal(RegistryArtifactTrustLevel.Unverified, result.TrustLevel);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("repository")]
    [InlineData("commit")]
    [InlineData("download")]
    [InlineData("hash")]
    [InlineData("size")]
    public void Validate_RejectsMutationOfEverySignedArtifactField(string field)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedPlugin(key);
        var tampered = field switch
        {
            "id" => signed with { Id = "com.typewhisper.changed" },
            "version" => signed with { Version = "2.0.0" },
            "source" => signed with { Source = "community" },
            "repository" => signed with { SourceRepository = "https://github.com/TypeWhisper/other" },
            "commit" => signed with
            {
                Attestation = signed.Attestation! with
                {
                    SourceCommit = new string('b', 40)
                }
            },
            "download" => signed with
            {
                DownloadUrl = "https://github.com/TypeWhisper/typewhisper-win/releases/download/v2/plugin.zip"
            },
            "hash" => signed with { Sha256 = new string('B', 64) },
            "size" => signed with { Size = signed.Size + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var result = CreateValidator(key).Validate(tampered);

        Assert.Equal(RegistryArtifactValidationCode.InvalidSignature, result.Code);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void Validate_RejectsUnsupportedTrustClaimBeforeSignatureEvaluation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(key) with
        {
            Trust = "publisher-asserted"
        };

        var result = CreateValidator(key).Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.UnsupportedTrust, result.Code);
        Assert.False(result.IsVerified);
    }

    [Theory]
    [InlineData("id", "../plugin", RegistryArtifactValidationCode.InvalidPluginId)]
    [InlineData("version", "preview", RegistryArtifactValidationCode.InvalidVersion)]
    [InlineData("key", "invalid key", RegistryArtifactValidationCode.InvalidKeyId)]
    public void Validate_RejectsMalformedCanonicalIdentityFields(
        string field,
        string value,
        RegistryArtifactValidationCode expectedCode)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedPlugin(key);
        var malformed = field switch
        {
            "id" => signed with { Id = value },
            "version" => signed with { Version = value },
            "key" => signed with
            {
                Attestation = signed.Attestation! with { KeyId = value }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var result = CreateValidator(key).Validate(malformed);

        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public void Validate_RejectsOfficialRepositoryOutsideTypeWhisperOrganization()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(key) with
        {
            SourceRepository = "https://github.com/attacker/typewhisper-win"
        };

        var result = CreateValidator(key).Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.InvalidSourceRepository, result.Code);
    }

    [Fact]
    public void Validate_RejectsMalformedSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plugin = CreateSignedPlugin(key) with
        {
            Attestation = CreateSignedPlugin(key).Attestation! with
            {
                Signature = "not-base64"
            }
        };

        var result = CreateValidator(key).Validate(plugin);

        Assert.Equal(RegistryArtifactValidationCode.InvalidSignature, result.Code);
    }

    [Fact]
    public void Constructor_RejectsDuplicateKeyIds()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<ArgumentException>(() => new RegistryArtifactTrustValidator([
            ExportTrustKey(first, "duplicate"),
            ExportTrustKey(second, "duplicate")
        ]));
    }

    [Fact]
    public void CreateCanonicalPayload_IsDeterministicAndExcludesDisplayMetadata()
    {
        var plugin = CreateUnsignedPlugin() with
        {
            Source = "official",
            Trust = RegistryArtifactTrustValidator.TypeWhisperBuiltTrust,
            SourceRepository = "https://github.com/TypeWhisper/typewhisper-win",
            Attestation = new RegistryArtifactAttestation
            {
                SchemaVersion = RegistryArtifactTrustValidator.SupportedSchemaVersion,
                Algorithm = RegistryArtifactTrustValidator.SupportedAlgorithm,
                KeyId = "test-key",
                SourceCommit = new string('a', 40),
                Signature = "pending"
            }
        };
        var renamed = plugin with
        {
            Name = "A different display name",
            Author = "A different display author",
            Description = "A different display description"
        };

        Assert.Equal(
            RegistryArtifactTrustValidator.CreateCanonicalPayload(plugin),
            RegistryArtifactTrustValidator.CreateCanonicalPayload(renamed));
    }

    internal static RegistryPlugin CreateSignedPlugin(
        ECDsa key,
        string keyId = "test-key",
        string source = "official",
        string sourceRepository = "https://github.com/TypeWhisper/typewhisper-win",
        string? packageSha256 = null,
        long packageSize = 1024)
    {
        var plugin = CreateUnsignedPlugin() with
        {
            Source = source,
            Trust = RegistryArtifactTrustValidator.TypeWhisperBuiltTrust,
            SourceRepository = sourceRepository,
            Sha256 = packageSha256 ?? new string('A', 64),
            Size = packageSize,
            Attestation = new RegistryArtifactAttestation
            {
                SchemaVersion = RegistryArtifactTrustValidator.SupportedSchemaVersion,
                Algorithm = RegistryArtifactTrustValidator.SupportedAlgorithm,
                KeyId = keyId,
                SourceCommit = new string('a', 40),
                Signature = "pending"
            }
        };
        var signature = key.SignData(
            RegistryArtifactTrustValidator.CreateCanonicalPayload(plugin),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return plugin with
        {
            Attestation = plugin.Attestation! with
            {
                Signature = Convert.ToBase64String(signature)
            }
        };
    }

    internal static RegistryArtifactTrustValidator CreateValidator(ECDsa key, string keyId = "test-key") =>
        new([ExportTrustKey(key, keyId)]);

    private static RegistryArtifactTrustKey ExportTrustKey(ECDsa key, string keyId) =>
        new(
            keyId,
            RegistryArtifactTrustValidator.SupportedAlgorithm,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    private static RegistryPlugin CreateUnsignedPlugin() => new()
    {
        Id = "com.typewhisper.example",
        Name = "Example Plugin",
        Version = "1.2.3",
        Author = "TypeWhisper",
        Description = "Example",
        Size = 1024,
        DownloadUrl = "https://github.com/TypeWhisper/typewhisper-win/releases/download/v1/plugin.zip",
        Sha256 = new string('A', 64)
    };
}
