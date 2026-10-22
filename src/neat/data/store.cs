using System.Text.Json;

namespace neat;

/// <summary>
/// Loads and saves <see cref="Prefs"/> as settings.json. It is one small
/// object, so plain JSON on disk is enough and everything is synchronous. That
/// also means a save started while the window is closing really finishes.
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    private readonly string _path;

    public Store(string path)
    {
        _path = path;
    }

    public Prefs Cur { get; private set; } = new();

    /// <summary>Raised after every successful <see cref="Save"/>.</summary>
    public event Action? Saved;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(_path), Opt);
                if (p is not null)
                {
                    Cur = p;
                    Repair();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // A damaged file must never stop the app from starting.
            System.Diagnostics.Debug.WriteLine("[store] load failed: " + ex.Message);
        }

        // The file was there but could not be used. Keep a copy before the
        // defaults replace it, so one typo in a hand edit does not cost every setting.
        KeepDamaged();

        Cur = new Prefs();
        Save();
    }

    private void KeepDamaged()
    {
        try
        {
            if (File.Exists(_path))
                File.Copy(_path, _path + ".bad", overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[store] could not keep the damaged file: " + ex.Message);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write beside the real file, then swap, so a crash mid-write
            // cannot leave a half-written settings.json behind.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Cur, Opt));
            File.Move(tmp, _path, overwrite: true);

            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[store] save failed: " + ex.Message);
        }
    }

    /// <summary>Puts back anything a hand-edited file left empty or out of range.</summary>
    private void Repair()
    {
        if (Cur.Engines is null || Cur.Engines.Count == 0)
            Cur.Engines = Engine.Defaults();

        if (string.IsNullOrWhiteSpace(Cur.Home))
            Cur.Home = new Prefs().Home;

        if (!Enum.IsDefined(Cur.Start))
            Cur.Start = StartMode.Home;

        Cur.Tint ??= new Tint();
        Cur.Tint.Hue = ((Cur.Tint.Hue % 360) + 360) % 360;
        Cur.Tint.Tone = Math.Clamp(Cur.Tint.Tone, 0, 1);
        Cur.Tint.Opacity = Math.Clamp(Cur.Tint.Opacity, Look.MinOpacity, Look.MaxOpacity);
        Cur.Tint.Texture = Grain.Clamp(Cur.Tint.Texture);
        if (!Harmonies.Valid(Cur.Tint.Harmony))
            Cur.Tint.Harmony = Harmony.Floating;

        Cur.Geo ??= new Geo();
        Cur.Geo.W = Math.Clamp(Cur.Geo.W, 640, 10000);
        Cur.Geo.H = Math.Clamp(Cur.Geo.H, 480, 10000);
    }
}
