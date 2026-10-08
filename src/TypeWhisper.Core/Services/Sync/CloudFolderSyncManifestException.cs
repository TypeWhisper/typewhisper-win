namespace TypeWhisper.Core.Services.Sync;

/// <summary>A shared sync manifest cannot be used safely and has been preserved.</summary>
public sealed class CloudFolderSyncManifestException : IOException
{
    /// <summary>Describes the manifest problem and a recovery action without replacing shared metadata.</summary>
    public CloudFolderSyncManifestException(string path, bool unsupportedFormat, Exception? inner = null)
        : base(unsupportedFormat
            ? Loc.T("The sync manifest at {0} uses an unsupported format. Update TypeWhisper on all devices or choose another sync folder. Existing data was kept.", path)
            : Loc.T("The sync manifest at {0} could not be read. Restore this file from a backup or choose another sync folder. Existing data was kept.", path), inner)
    {
        UnsupportedFormat = unsupportedFormat;
    }

    /// <summary>Whether the JSON was readable but identifies an unsupported package format.</summary>
    public bool UnsupportedFormat { get; }
}
