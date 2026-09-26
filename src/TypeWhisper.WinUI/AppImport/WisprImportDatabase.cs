using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.WinUI;

internal sealed record WisprImportRow(string Phrase, string? Replacement, bool Deleted, bool Snippet);

internal static class WisprImportDatabase
{
    internal const int MaximumRows = 10000;
    private const string Library = "winsqlite3.dll";
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_open_v2(byte[] path, out nint db, int flags, nint vfs);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_close_v2(nint db);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_exec(nint db, byte[] sql, nint callback, nint argument, nint error);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_prepare_v2(nint db, byte[] sql, int length, out nint statement, nint tail);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_step(nint statement);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_finalize(nint statement);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_column_type(nint statement, int column);
    [DllImport(Library, ExactSpelling = true)] private static extern nint sqlite3_column_text16(nint statement, int column);
    [DllImport(Library, ExactSpelling = true)] private static extern int sqlite3_column_bytes16(nint statement, int column);
    [DllImport(Library, ExactSpelling = true)] private static extern long sqlite3_column_int64(nint statement, int column);

    internal static IReadOnlyList<WisprImportRow> Read(string source) => StableImportCopy.Read(source, ReadCopy);

    private static IReadOnlyList<WisprImportRow> ReadCopy(string copy)
    {
        // READWRITE without CREATE permits rebuilding the WAL index, exclusively inside our scratch folder.
        var opened = sqlite3_open_v2(Utf8(copy), out var db, 2, 0);
        try
        {
            if (opened != 0) throw Unreadable();
            if (sqlite3_exec(db, Utf8("PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;"), 0, 0, 0) != 0) throw Unreadable();
            var sql = $"SELECT phrase, replacement, isDeleted, isSnippet FROM Dictionary ORDER BY id COLLATE BINARY LIMIT {MaximumRows + 1}";
            var prepared = sqlite3_prepare_v2(db, Utf8(sql), -1, out var statement, 0);
            try
            {
                if (prepared != 0) throw Unreadable();
                var rows = new List<WisprImportRow>();
                int result;
                while ((result = sqlite3_step(statement)) == 100)
                {
                    if (rows.Count == MaximumRows) throw new InvalidDataException($"The source has more than {MaximumRows:N0} entries. Nothing was imported.");
                    rows.Add(new(Text(statement, 0)!, Text(statement, 1, optional: true), Boolean(statement, 2), Boolean(statement, 3)));
                }
                if (result != 101) throw Unreadable();
                return rows;
            }
            finally { if (statement != 0) sqlite3_finalize(statement); }
        }
        finally { if (db != 0) sqlite3_close_v2(db); }
    }

    private static string? Text(nint statement, int column, bool optional = false)
    {
        var type = sqlite3_column_type(statement, column);
        if (optional && type == 5) return null;
        if (type != 3) throw Unreadable();
        var length = sqlite3_column_bytes16(statement, column);
        if (length > 20000) throw new InvalidDataException("A source entry exceeds 10,000 characters. Nothing was imported.");
        return Marshal.PtrToStringUni(sqlite3_column_text16(statement, column), length / 2) ?? throw Unreadable();
    }

    private static bool Boolean(nint statement, int column)
    {
        if (sqlite3_column_type(statement, column) != 1) throw Unreadable();
        return sqlite3_column_int64(statement, column) switch { 0 => false, 1 => true, _ => throw Unreadable() };
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');
    private static IOException Unreadable() => new("Could not read the Wispr Flow database format. Quitting Wispr Flow and trying again can help. Nothing was imported.");
}
