using System.IO;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Represents an unavailable custom local model storage path.
/// </summary>
public sealed class LocalModelStorageUnavailableException : IOException
{
    /// <summary>
    /// Initializes a new instance of the LocalModelStorageUnavailableException class.
    /// </summary>
    public LocalModelStorageUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the LocalModelStorageUnavailableException class.
    /// </summary>
    public LocalModelStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
