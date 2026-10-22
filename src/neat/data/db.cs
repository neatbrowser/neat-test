using Microsoft.Data.Sqlite;

namespace neat;

/// <summary>
/// Hands out connections to browser.db. History and bookmarks each own a table
/// in the same file. Connections are cheap and short lived, so every call opens
/// its own and disposes it.
/// </summary>
public sealed class Db
{
    private readonly string _cs;

    public Db(string path)
    {
        _cs = $"Data Source={path}";
    }

    public async Task<SqliteConnection> Open()
    {
        var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        return c;
    }
}
