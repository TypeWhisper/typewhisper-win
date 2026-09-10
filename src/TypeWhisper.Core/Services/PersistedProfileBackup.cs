using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Models.Backup;

namespace TypeWhisper.Core.Services;

/// <summary>An opaque, reviewed merge tied to the exact destination snapshot.</summary>
public sealed class PersistedProfileBackupPreview
{
    internal PersistedProfileBackupPreview(Guid owner, Dictionary<string, byte[]?> originals,
        Dictionary<string, byte[]> candidates, BackupImportResult merge)
    { Owner = owner; Originals = originals; Candidates = candidates; Merge = merge; }
    internal Guid Owner { get; }
    internal Dictionary<string, byte[]?> Originals { get; }
    internal Dictionary<string, byte[]> Candidates { get; }
    /// <summary>Per-category merge counts, conflicts, and warnings from the portable backup service.</summary>
    public BackupImportResult Merge { get; }
    /// <summary>Number of named profile files that would change.</summary>
    public int ChangedFileCount => Candidates.Count;
}

/// <summary>Whether restoration changed data and whether startup must remain blocked pending recovery.</summary>
public sealed record PersistedProfileBackupCommitResult(bool Applied, bool RecoveryRequired, string? Error);

/// <summary>A startup gate result; false prohibits opening any live profile stores or writers.</summary>
public sealed record PersistedProfileBackupRecoveryResult(bool CanOpenProfile, string? Error);

/// <summary>Stages portable category merges and publishes only four fixed profile files using a recoverable journal.</summary>
/// <remarks>The host must stop all profile consumers before Apply, restart/reload them after success, and call RecoverPending before creating stores at startup.</remarks>
public sealed class PersistedProfileBackup
{
    /// <summary>The supported categories; device settings, credentials, audio and model assets are excluded.</summary>
    public const BackupCategory SupportedCategories = BackupCategory.Dictionary | BackupCategory.Snippets | BackupCategory.Workflows | BackupCategory.History;
    private static readonly IReadOnlyDictionary<BackupCategory, string> Names = new Dictionary<BackupCategory, string>
    {
        [BackupCategory.Dictionary] = "dictionary.json", [BackupCategory.Snippets] = "snippets.json",
        [BackupCategory.Workflows] = "workflows.json", [BackupCategory.History] = "history.json"
    };
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly string _root;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Action<string>? _checkpoint;
    private string TransactionDirectory => Path.Combine(_root, ".profile-restore");
    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private sealed record Journal(
        [property: JsonRequired] int Version,
        [property: JsonRequired] bool Committed,
        [property: JsonRequired] bool RolledBack,
        [property: JsonRequired] IReadOnlyList<JournalFile> Files);
    private sealed record JournalFile(
        [property: JsonRequired] string Name,
        [property: JsonRequired] string? OriginalHash,
        [property: JsonRequired] string CandidateHash);

    /// <summary>Uses an explicit isolated profile root. The optional checkpoint callback supports deterministic filesystem failure tests.</summary>
    public PersistedProfileBackup(string root, Action<string>? checkpoint = null)
    { _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); _checkpoint = checkpoint; }

    /// <summary>Whether a transaction directory requires recovery before any profile writer starts.</summary>
    public bool HasPendingRecovery => Directory.Exists(TransactionDirectory) || File.Exists(TransactionDirectory);

    /// <summary>Exports only selected categories in the existing portable TypeWhisper JSON format.</summary>
    public async Task<string> ExportAsync(BackupCategory categories, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateCategories(categories);
        var originals = CaptureProfile();
        var staging = CreateStage(originals);
        try
        {
            var json = await CreateService(staging).ExportAsync(new() { Categories = categories }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("The selected backup exceeds the 64 MiB limit.");
            return json;
        }
        finally { DeleteStage(staging); }
    }

    /// <summary>Validates a portable archive and merges into a private staging profile; the real profile remains unchanged.</summary>
    public async Task<PersistedProfileBackupPreview> PreviewAsync(string json, BackupCategory categories, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateCategories(categories);
        var document = ParseStrict<SettingsBackupDocument>(Encoding.UTF8.GetBytes(json));
        if (document.Format != SettingsBackupDocument.CurrentFormat || document.SchemaVersion != SettingsBackupDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported TypeWhisper backup format or version.");
        var originals = CaptureProfile();
        var staging = CreateStage(originals);
        try
        {
            var service = CreateService(staging);
            var validation = service.PreviewImport(json);
            if (!validation.IsValid) throw new InvalidDataException(validation.Error);
            if (categories.HasFlag(BackupCategory.Dictionary) && document.Data.Dictionary.EnabledPackIds.Any(id =>
                TermPack.FindById(id) is not { RequiresCommercialLicense: false }))
                throw new InvalidDataException("This backup requires an unknown or licensed term pack. Restore the other categories or install that pack separately.");
            var merge = await service.ImportAsync(json, new() { Categories = categories }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!merge.Success) throw new InvalidDataException(merge.Error);
            var candidates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var pair in Names.Where(pair => categories.HasFlag(pair.Key)))
            {
                var bytes = ReadBounded(Path.Combine(staging, pair.Value));
                ValidateProfileFile(pair.Value, bytes);
                if (originals[pair.Value] is null && ParseStrict<JsonElement>(bytes).GetArrayLength() == 0) continue;
                if (!BytesEqual(originals[pair.Value], bytes)) candidates.Add(pair.Value, bytes);
            }
            if (document.Data.Plugins.Count > 0 || document.Data.Hotkeys.Bindings.Count > 0 || document.Data.Preferences is not null)
                merge = merge with { Warnings = merge.Warnings.Append("Plugins, shortcuts and preferences are not part of this restore.").ToArray() };
            return new(_owner, originals, candidates, merge);
        }
        finally { DeleteStage(staging); }
    }

    /// <summary>Publishes a reviewed snapshot only while the host has quiesced live consumers. Changed destinations require a new preview.</summary>
    public PersistedProfileBackupCommitResult Apply(PersistedProfileBackupPreview preview)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        var prepared = false;
        var committed = false;
        var ownsPreparation = false;
        Journal? journal = null;
        try
        {
            CheckRoot();
            if (HasPendingRecovery) throw new InvalidOperationException("Resolve the pending profile restore before continuing.");
            if (preview.Owner != _owner) throw new ArgumentException("The restore preview belongs to another profile session.", nameof(preview));
            foreach (var pair in preview.Originals)
                if (!BytesEqual(ReadOptional(Target(pair.Key)), pair.Value)) throw new InvalidOperationException("Profile data changed after preview. Review the backup again.");
            if (preview.Candidates.Count == 0) return new(false, false, null);
            Directory.CreateDirectory(TransactionDirectory);
            ownsPreparation = true;
            var files = preview.Candidates.Select(pair => new JournalFile(pair.Key, Hash(preview.Originals[pair.Key]), Hash(pair.Value)!)).ToList();
            journal = new(1, false, false, files);
            WriteJournal(journal); prepared = true;
            foreach (var pair in preview.Candidates)
            {
                if (preview.Originals[pair.Key] is { } original) AtomicWrite(Artifact("original-", pair.Key), original);
                AtomicWrite(Artifact("candidate-", pair.Key), pair.Value);
            }
            _checkpoint?.Invoke("Prepared");
            foreach (var file in files)
            {
                _checkpoint?.Invoke("BeforePublish:" + file.Name);
                var candidate = ReadBounded(Artifact("candidate-", file.Name));
                if (Hash(candidate) != file.CandidateHash) throw new IOException("A staged restore file failed verification.");
                AtomicWrite(Target(file.Name), candidate);
            }
            WriteJournal(journal with { Committed = true }); committed = true;
            _checkpoint?.Invoke("Committed");
            Cleanup(journal);
            return new(true, false, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (committed) return new(true, true, "Restore committed, but cleanup failed. Keep profile writers stopped: " + ex.Message);
            if (prepared && journal is not null)
            {
                try { Rollback(journal); WriteJournal(journal with { RolledBack = true }); Cleanup(journal); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException)
                { return new(false, true, "Restore and rollback did not finish. Profile access must remain blocked: " + rollback.Message); }
            }
            else if (ownsPreparation && Directory.Exists(TransactionDirectory))
            {
                // No live file is touched until the durable Prepared journal exists.
                // Unknown content is never removed by this cleanup.
                try { CleanupKnownPreparation(); }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                { return new(false, true, "Restore preparation cleanup failed. Profile access must remain blocked: " + cleanup.Message); }
            }
            return new(false, HasPendingRecovery, "Restore was not applied: " + ex.Message);
        }
    }

    /// <summary>Rolls back an interrupted publication or completes committed cleanup. Failure is a mandatory startup stop.</summary>
    public PersistedProfileBackupRecoveryResult RecoverPending()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        try
        {
            CheckRoot();
            if (!HasPendingRecovery) return new(true, null);
            RejectLink(TransactionDirectory);
            var journalPath = Path.Combine(TransactionDirectory, "journal.json");
            if (!File.Exists(journalPath))
            {
                // Before the first journal rename no profile file has been touched.
                // After the last journal deletion only an empty directory remains.
                var leftovers = Directory.EnumerateFileSystemEntries(TransactionDirectory).ToArray();
                if (leftovers.Any(path => !IsTemporaryFor(Path.GetFileName(path), "journal.json") || Directory.Exists(path)))
                    throw new IOException("The restore journal is missing; unknown recovery files were preserved.");
                foreach (var path in leftovers) { RejectLink(path); DeleteArtifact(path); }
                Directory.Delete(TransactionDirectory);
                return new(true, null);
            }
            var journal = ParseStrict<Journal>(ReadBounded(journalPath));
            ValidateJournal(journal);
            if (!journal.Committed && !journal.RolledBack)
            {
                Rollback(journal);
                WriteJournal(journal with { RolledBack = true });
            }
            else foreach (var file in journal.Files)
                if (Hash(ReadOptional(Target(file.Name))) != (journal.Committed ? file.CandidateHash : file.OriginalHash))
                    throw new IOException("A completed restore destination changed before recovery.");
            Cleanup(journal);
            return new(true, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, "Profile restore recovery did not finish. Do not open profile stores: " + ex.Message); }
    }

    private Dictionary<string, byte[]?> CaptureProfile()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        CheckRoot();
        if (HasPendingRecovery) throw new InvalidOperationException("Recover the pending profile restore before reading profile data.");
        var files = new Dictionary<string, byte[]?>();
        foreach (var name in Names.Values)
        {
            var bytes = ReadOptional(Target(name));
            if (bytes is not null) ValidateProfileFile(name, bytes);
            files.Add(name, bytes);
        }
        return files;
    }

    private string CreateStage(Dictionary<string, byte[]?> files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".backup-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { foreach (var pair in files) File.WriteAllBytes(Path.Combine(path, pair.Key), pair.Value ?? "[]"u8.ToArray()); return path; }
        catch { DeleteStage(path); throw; }
    }

    private BackupRestoreService CreateService(string staging)
    {
        var dictionary = new DictionaryService(Path.Combine(staging, "dictionary.json"));
        var settings = new MemorySettings(new AppSettings
        {
            HistoryRetentionMode = HistoryRetentionMode.Forever,
            EnabledPackIds = TermPack.AllPacks.Where(pack => dictionary.Entries.Any(entry => entry.Id.StartsWith("pack:" + pack.Id + ":", StringComparison.Ordinal)))
                .Select(pack => pack.Id).ToArray()
        });
        return new(settings, new WorkflowService(Path.Combine(staging, "workflows.json")), dictionary,
            new SnippetService(Path.Combine(staging, "snippets.json")), new HistoryService(Path.Combine(staging, "history.json")) { ThrowOnLoadFailure = true });
    }

    private void Rollback(Journal journal)
    {
        ValidateJournal(journal);
        foreach (var file in journal.Files)
        {
            var current = Hash(ReadOptional(Target(file.Name)));
            if (current != file.OriginalHash && current != file.CandidateHash) throw new IOException("A destination changed outside the restore transaction: " + file.Name);
            if (current != file.OriginalHash && file.OriginalHash is not null && Hash(ReadBounded(Artifact("original-", file.Name))) != file.OriginalHash)
                throw new IOException("An original restore copy failed verification: " + file.Name);
        }
        foreach (var file in journal.Files)
        {
            _checkpoint?.Invoke("BeforeRollback:" + file.Name);
            if (Hash(ReadOptional(Target(file.Name))) == file.OriginalHash) continue;
            if (file.OriginalHash is null) File.Delete(Target(file.Name));
            else AtomicWrite(Target(file.Name), ReadBounded(Artifact("original-", file.Name)));
        }
    }

    private void Cleanup(Journal journal)
    {
        var allowed = journal.Files.SelectMany(file => new[] { "original-" + file.Name, "candidate-" + file.Name })
            .Append("journal.json").ToHashSet(StringComparer.Ordinal);
        var paths = Directory.EnumerateFileSystemEntries(TransactionDirectory).ToArray();
        if (paths.Any(path => Directory.Exists(path) || (!allowed.Contains(Path.GetFileName(path)) && !allowed.Any(name => IsTemporaryFor(Path.GetFileName(path), name)))))
            throw new IOException("Unknown files in the restore transaction were preserved.");
        foreach (var path in paths) RejectLink(path);
        foreach (var file in journal.Files)
        { DeleteArtifact(Artifact("original-", file.Name)); DeleteArtifact(Artifact("candidate-", file.Name)); }
        foreach (var path in paths.Where(path => !allowed.Contains(Path.GetFileName(path)))) DeleteArtifact(path);
        DeleteArtifact(Path.Combine(TransactionDirectory, "journal.json"));
        Directory.Delete(TransactionDirectory);
    }
    private static bool IsTemporaryFor(string filename, string destination) =>
        filename.Length == destination.Length + 37 && filename.StartsWith(destination + ".", StringComparison.Ordinal) && filename.EndsWith(".tmp", StringComparison.Ordinal) &&
        Guid.TryParseExact(filename[(destination.Length + 1)..^4], "N", out _);
    private void CleanupKnownPreparation()
    {
        RejectLink(TransactionDirectory);
        foreach (var name in Names.Values)
        { DeleteArtifact(Artifact("original-", name)); DeleteArtifact(Artifact("candidate-", name)); }
        Directory.Delete(TransactionDirectory);
    }
    private void WriteJournal(Journal journal) => AtomicWrite(Path.Combine(TransactionDirectory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal, Strict));
    private string Target(string name) => Names.Values.Contains(name, StringComparer.Ordinal) ? Path.Combine(_root, name) : throw new InvalidDataException("Unsupported profile file.");
    private string Artifact(string prefix, string name) { _ = Target(name); return Path.Combine(TransactionDirectory, prefix + name); }

    private static void ValidateCategories(BackupCategory categories)
    { if (categories == BackupCategory.None || (categories & ~SupportedCategories) != 0) throw new ArgumentException("Choose dictionary, snippets, workflows or history.", nameof(categories)); }
    private static void ValidateJournal(Journal journal)
    {
        if (journal.Version != 1 || journal.Committed && journal.RolledBack || journal.Files is null || journal.Files.Count is < 1 or > 4 ||
            journal.Files.Any(file => file is null || !Names.Values.Contains(file.Name, StringComparer.Ordinal) ||
                !ValidHash(file.CandidateHash) || file.OriginalHash is not null && !ValidHash(file.OriginalHash)) ||
            journal.Files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != journal.Files.Count)
            throw new InvalidDataException("Invalid restore journal.");
    }
    private static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool BytesEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private void CheckRoot() { if (File.Exists(_root)) throw new IOException("The profile root is not a directory."); if (Directory.Exists(_root)) RejectLink(_root); }
    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked profile and transaction paths are not supported."); }
    private static byte[]? ReadOptional(string path)
    { if (!File.Exists(path)) { if (Directory.Exists(path)) throw new IOException("A profile file is a directory: " + Path.GetFileName(path)); return null; } return ReadBounded(path); }
    private static byte[] ReadBounded(string path)
    {
        RejectLink(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("A backup or profile file exceeds the 64 MiB limit.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        if (File.Exists(path) || Directory.Exists(path)) RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void DeleteArtifact(string path) { if (!File.Exists(path)) return; RejectLink(path); File.Delete(path); }
    private void DeleteStage(string path)
    {
        if (Path.GetDirectoryName(path) != _root || !Path.GetFileName(path).StartsWith(".backup-preview-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid staging path.");
        RejectLink(path);
        foreach (var file in Directory.EnumerateFiles(path)) { RejectLink(file); File.Delete(file); }
        Directory.Delete(path);
    }
    private static T ParseStrict<T>(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Backup metadata exceeds the size limit.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicates(json.RootElement);
        if (typeof(T) == typeof(SettingsBackupDocument))
        {
            var fields = json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!fields.Contains("format") || !fields.Contains("schemaVersion") || !fields.Contains("data") ||
                !fields.Contains("sourcePlatform") || !fields.Contains("exportedAt"))
                throw new InvalidDataException("Backup format, version, source, timestamp and data are required.");
        }
        return json.RootElement.Deserialize<T>(Strict) ?? throw new InvalidDataException("Backup metadata is empty.");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON field."); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }
    private static void ValidateProfileFile(string name, byte[] bytes)
    {
        IEnumerable<string> ids = name switch
        {
            "dictionary.json" => ParseStrict<DictionaryEntry[]>(bytes).Select(entry => entry?.Id ?? ""),
            "snippets.json" => ParseStrict<Snippet[]>(bytes).Select(entry => entry?.Id ?? ""),
            "workflows.json" => ParseStrict<Workflow[]>(bytes).Select(entry => entry?.Id ?? ""),
            "history.json" => ParseStrict<TranscriptionRecord[]>(bytes).Select(entry => entry?.Id ?? ""),
            _ => throw new InvalidDataException("Unknown profile file.")
        };
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || !unique.Add(id))) throw new InvalidDataException("Invalid or duplicate profile identity.");
    }
    private sealed class MemorySettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) { Current = settings; SettingsChanged?.Invoke(settings); }
    }
}
