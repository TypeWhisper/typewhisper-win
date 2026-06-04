using System.IO;

namespace TypeWhisper.Windows.Services;

public sealed class LocalModelStorageUnavailableException : IOException
{
    public LocalModelStorageUnavailableException(string message)
        : base(message)
    {
    }

    public LocalModelStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
