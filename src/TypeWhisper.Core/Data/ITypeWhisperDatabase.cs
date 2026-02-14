using Microsoft.Data.Sqlite;

namespace TypeWhisper.Core.Data;

public interface ITypeWhisperDatabase : IDisposable
{
    SqliteConnection GetConnection();
    void Initialize();
}
