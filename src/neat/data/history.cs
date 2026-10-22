using Microsoft.Data.Sqlite;

namespace neat;

public sealed class Visit
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The "history" table in browser.db. Same table layout as Mozart's, so a
/// Mozart browser.db can be copied over later. Private tabs must never call
/// in here; that check belongs at the call site, so this class knows nothing
/// about private mode.
/// </summary>
public sealed class History
{
    private const string Cols = "id, url, title, favicon_url, visited_at";

    private readonly Db _db;

    public History(Db db)
    {
        _db = db;
    }

    public async Task Init()
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                favicon_url TEXT,
                visited_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_visited_at ON history(visited_at);
            CREATE INDEX IF NOT EXISTS idx_history_url ON history(url);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Records a visit. Failing to record must never get in the way of browsing, so errors are only logged.</summary>
    public async Task Add(string url, string? title, string? icon)
    {
        try
        {
            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history (url, title, favicon_url, visited_at)
                VALUES ($url, $title, $icon, $at);
                """;
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$title", title ?? string.Empty);
            cmd.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[history] add failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Newest first. offset skips that many visits, so a list can be read a page at a time.
    /// The id breaks ties, so two visits at the same instant keep the same order between pages.
    /// </summary>
    public async Task<List<Visit>> Recent(int limit = 200, int offset = 0)
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM history ORDER BY visited_at DESC, id DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return await Read(cmd);
    }

    /// <summary>Case-insensitive substring match on address or title, newest first. offset: see Recent.</summary>
    public async Task<List<Visit>> Find(string query, int limit = 200, int offset = 0)
    {
        // Escape LIKE's own wildcards so "50%" or "a_b" are searched literally.
        var like = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Cols} FROM history
            WHERE url LIKE $like ESCAPE '\' OR title LIKE $like ESCAPE '\'
            ORDER BY visited_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$like", like);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return await Read(cmd);
    }

    public async Task Delete(int id)
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task Clear()
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> Count()
    {
        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM history;";
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<List<Visit>> Read(SqliteCommand cmd)
    {
        var all = new List<Visit>();

        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            all.Add(new Visit
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
}
