using System.IO;
using System.Net.Http;
using System.Text.Json;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.Platform.Tests;

// license.json held the key in plain text. It moves into the encrypted licenses.dat and is then deleted.
public sealed class LicenseLegacyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "license-legacy-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_directory, "license.json");
    private string Encrypted => Path.Combine(_directory, "licenses.dat");

    public LicenseLegacyStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteLegacy() => File.WriteAllText(Legacy, JsonSerializer.Serialize(new
    {
        key = "TEST-LEGACY-KEY", activationId = "legacy-activation", status = "Active", tier = "Gold",
        lastValidated = DateTime.UtcNow.AddDays(-1).ToString("o")
    }));

    [Fact]
    public void MigrationWritesTheEncryptedStoreAndDeletesThePlainTextFile()
    {
        WriteLegacy();
        using var http = new HttpClient();

        var service = new LicenseService(http, _directory);

        Assert.True(service.HasSupporterActivation);
        Assert.Equal(LicenseStatus.Active, service.SupporterStatus);
        Assert.True(File.Exists(Encrypted));
        Assert.DoesNotContain("TEST-LEGACY-KEY", File.ReadAllText(Encrypted));
        Assert.False(File.Exists(Legacy));

        var restarted = new LicenseService(http, _directory);
        Assert.True(restarted.HasSupporterActivation);
        Assert.Equal(LicenseStatus.Active, restarted.SupporterStatus);
    }

    [Fact]
    public void APlainTextFileLeftBesideTheEncryptedStoreIsDeleted()
    {
        WriteLegacy();
        using var http = new HttpClient();
        _ = new LicenseService(http, _directory);
        // As an earlier version left it: migrated, but still on disk.
        WriteLegacy();

        var service = new LicenseService(http, _directory);

        Assert.True(service.HasSupporterActivation);
        Assert.False(File.Exists(Legacy));
    }

    [Fact]
    public void TheMigrationKeepsThePlainTextFileWhenTheEncryptedStoreCannotBeWritten()
    {
        WriteLegacy();
        // A folder in place of the store file makes the write fail.
        Directory.CreateDirectory(Encrypted);
        using var http = new HttpClient();

        var service = new LicenseService(http, _directory);

        Assert.True(service.HasSupporterActivation);
        Assert.NotNull(service.StorageError);
        Assert.True(File.Exists(Legacy));
    }

    [Theory]
    [InlineData("2026-11-12T10:00:00.0000000Z")]
    [InlineData("2026-11-12T11:00:00.0000000+01:00")]
    public void CheckTimesAreReadInUtc(string value)
    {
        var parsed = LicenseService.ParseValidationTime(value);
        Assert.Equal(new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        Assert.Null(LicenseService.ParseValidationTime("not a date"));
        Assert.Null(LicenseService.ParseValidationTime(null));
    }
}
