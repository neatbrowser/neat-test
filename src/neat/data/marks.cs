using Microsoft.Data.Sqlite;

namespace neat;

public sealed class Mark
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The "bookmarks" table in browser.db: a flat list, no folders yet. Table and
/// column names match Mozart's, so a Mozart browser.db can be copied over later.
/// </summary>
public sealed class Bookmarks
{
    private readonly Db _db;

    public Bookmarks(Db db)
    {
        _db = db;
    }

    public async Task Init()
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS bookmarks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT,
                folder_id INTEGER,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_bookmarks_url ON bookmarks(url);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> Has(string url)
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bookmarks WHERE url = $url;";
        cmd.Parameters.AddWithValue("$url", url);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
    }

    public async Task Add(string url, string? title, string? icon)
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bookmarks (url, title, favicon_url, created_at)
            VALUES ($url, $title, $icon, $at);
            """;
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$title", title ?? string.Empty);
        cmd.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task Remove(string url)
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM bookmarks WHERE url = $url;";
        cmd.Parameters.AddWithValue("$url", url);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Newest first.</summary>
    public async Task<List<Mark>> All()
    {
        var all = new List<Mark>();

        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, url, title, favicon_url, created_at
            FROM bookmarks
            ORDER BY created_at DESC;
            """;

        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            all.Add(new Mark
            {
                Id = r.GetInt32(0),
                Url = r.GetString(1),
                Title = r.GetString(2),
                Icon = r.IsDBNull(3) ? null : r.GetString(3),
                At = DateTime.Parse(r.GetString(4)),
            });
        }

        return all;
    }

    public async Task<long> Count()
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bookmarks;";
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
