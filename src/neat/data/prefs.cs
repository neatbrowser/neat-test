namespace neat;

/// <summary>One search engine the address box can use.</summary>
public sealed class Engine
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Address template; %s stands for the URL-encoded query.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Short prefix for a one-off search: "!b cats" uses the engine whose key is "b".</summary>
    public string Key { get; set; } = string.Empty;

    public static List<Engine> Defaults() => new()
    {
        new Engine { Name = "Google",     Url = "https://www.google.com/search?q=%s", Key = "g" },
        new Engine { Name = "Bing",       Url = "https://www.bing.com/search?q=%s",   Key = "b" },
        new Engine { Name = "DuckDuckGo", Url = "https://duckduckgo.com/?q=%s",       Key = "d" },
        new Engine { Name = "Cốc Cốc",    Url = "https://coccoc.com/search?query=%s", Key = "c" },
    };
}

/// <summary>Where the window was last time, so it opens the same way.</summary>
public sealed class Geo
{
    public int W { get; set; } = 1280;
    public int H { get; set; } = 800;
    public bool Max { get; set; }
}

/// <summary>
/// The window colour: the spots picked on the colour pad and how the colour is
/// shaped. Hue and Tone place the first dot; the harmony says how many dots
/// there are and works out where the others go. The gradient is made from
/// these (see look.cs).
/// </summary>
public sealed class Tint
{
    /// <summary>Angle of the first dot round the pad, 0 to 360 degrees. It decides the colour.</summary>
    public double Hue { get; set; } = 75;

    /// <summary>Distance of the first dot from the pad's centre, 0 (centre, darkest) to 1 (rim, lightest).</summary>
    public double Tone { get; set; } = 0.38;

    /// <summary>How the dots on the pad relate to each other; it also decides how many there are.</summary>
    public Harmony Harmony { get; set; } = Harmony.Floating;

    /// <summary>How strongly the picked colour shows over the window's base colour, 0.25 to 0.8.</summary>
    public double Opacity { get; set; } = 0.5;

    /// <summary>How strong the grain (a layer of fine noise over the window colour) is: 0 for none, up to 15. See grain.cs.</summary>
    public int Texture { get; set; } = 0;

    /// <summary>Dark or light window.</summary>
    public bool Dark { get; set; } = true;
}

/// <summary>What the browser opens when it starts (Settings > General > On startup).</summary>
public enum StartMode
{
    /// <summary>The home page, in one tab.</summary>
    Home,

    /// <summary>The tabs that were open when the browser was closed (see session.cs).</summary>
    Last,
}

/// <summary>
/// Everything saved in settings.json. A plain data object with no logic, so it
/// round-trips cleanly through System.Text.Json. Unknown keys in the file are
/// ignored, so new settings can be added in later phases without breaking old files.
/// </summary>
public sealed class Prefs
{
    /// <summary>Page the first tab opens on at startup.</summary>
    public string Home { get; set; } = "https://www.google.com";

    /// <summary>What opens at startup: the home page, or the tabs of last time.</summary>
    public StartMode Start { get; set; } = StartMode.Home;

    /// <summary>Name of the engine in use. Must match one of Engines.</summary>
    public string Use { get; set; } = "Google";

    public List<Engine> Engines { get; set; } = Engine.Defaults();

    public Geo Geo { get; set; } = new();

    public Tint Tint { get; set; } = new();

    /// <summary>True when the sidebar is docked beside the page, false when it is hidden.</summary>
    public bool Dock { get; set; } = true;

    /// <summary>The ad blocker's settings (ads/ads.cs).</summary>
    public AdPrefs Ads { get; set; } = new();
}

/// <summary>Settings of the ad blocker.</summary>
public sealed class AdPrefs
{
    /// <summary>False turns the blocker off everywhere. Read once at start, so it applies after a restart.</summary>
    public bool On { get; set; } = true;

    /// <summary>Hosts (like "example.com") where the blocker does nothing.</summary>
    public List<string> Off { get; set; } = new();

    /// <summary>
    /// True writes one line per request to ads-requests.tsv next to settings.json (what was asked,
    /// what was answered, what was skipped and why). For finding out why a host got through. Read
    /// once at start, so it applies after a restart.
    /// </summary>
    public bool Log { get; set; }

    /// <summary>
    /// True lets the blocker look for newer filter lists by itself and use them (the lists that
    /// ship with the app are from the day it was built). Read once at start.
    /// </summary>
    public bool Update { get; set; } = true;

    /// <summary>Hours between two looks for newer lists (1 to 168). The lists themselves are rebuilt every 6 hours.</summary>
    public int UpdateHours { get; set; } = 6;

    /// <summary>The GitHub repository whose latest release holds the lists (its web address; the API is not used).</summary>
    public string UpdateRepo { get; set; } = "https://github.com/neatbrowser/neat-filters";
}
