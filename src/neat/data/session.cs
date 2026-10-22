using System.Text.Json;

namespace neat;

/// <summary>The tabs that were open: their addresses, and which one was selected.</summary>
public sealed class Snap
{
    public List<string> Urls { get; set; } = new();

    /// <summary>Position in <see cref="Urls"/> of the selected tab.</summary>
    public int Active { get; set; }
}

/// <summary>
/// Keeps the open tabs in session.json, so "Continue where you left off" (Settings > General)
/// can open them again at the next start. It lives in a file of its own, not in settings.json,
/// because it changes with every page you open and settings.json is only written when a
/// setting changes. Like <see cref="Store"/> it is plain synchronous JSON, written beside the
/// real file and then swapped in, so a crash in the middle cannot leave half a file.
/// </summary>
public sealed class Session
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    private readonly string _path;

    public Session(string path)
    {
        _path = path;
    }

    /// <summary>
    /// The saved tabs. A missing or damaged file gives an empty set: a session that cannot be
    /// read must never stop the browser from starting.
    /// </summary>
    public Snap Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<Snap>(File.ReadAllText(_path), Opt);
                if (s is not null)
                {
                    s.Urls ??= new List<string>();
                    s.Urls.RemoveAll(string.IsNullOrWhiteSpace);
                    s.Active = Math.Clamp(s.Active, 0, Math.Max(0, s.Urls.Count - 1));
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("session", ex, "load failed");
        }

        return new Snap();
    }

    public void Save(Snap s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opt));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("session", ex, "save failed");
        }
    }
}
