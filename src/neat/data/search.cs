using System.Net;

namespace neat;

/// <summary>
/// Turns whatever was typed in the address box into something navigable: the
/// address itself when it looks like one, otherwise a search with the engine
/// in use. "!b cats" forces the engine whose key is "b" for that one search.
/// </summary>
public sealed class Search
{
    private readonly Store _cfg;

    public Search(Store cfg)
    {
        _cfg = cfg;
    }

    /// <summary>The engine named in settings, or the first one if the name matches nothing.</summary>
    public Engine Use =>
        _cfg.Cur.Engines.FirstOrDefault(e => e.Name == _cfg.Cur.Use)
        ?? _cfg.Cur.Engines.FirstOrDefault()
        ?? Engine.Defaults()[0];

    /// <summary>Returns an address to navigate to, or null when there is nothing to go to.</summary>
    public string? Resolve(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        if (s.StartsWith('!'))
        {
            var sp = s.IndexOf(' ');
            if (sp > 1)
            {
                var key = s[1..sp];
                var q = s[(sp + 1)..];
                var e = _cfg.Cur.Engines.FirstOrDefault(
                    x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (e is not null)
                    return Build(e, q);
            }
        }

        if (Looks(s))
            return Fix(s);

        return Build(Use, s);
    }

    /// <summary>True when <see cref="Resolve"/> would open the text as an address, false when it would search for it.</summary>
    public bool IsAddress(string? text)
    {
        var s = text?.Trim();
        return !string.IsNullOrEmpty(s) && Looks(s);
    }

    private static string Build(Engine e, string q)
    {
        return e.Url.Replace("%s", WebUtility.UrlEncode(q));
    }

    private static bool HasScheme(string s)
    {
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The test every address box uses: a scheme, or "word.word" with an
    /// optional path and no spaces, or localhost / a bare local IP.
    /// </summary>
    private static bool Looks(string s)
    {
        if (s.Contains(' '))
            return false;

        if (HasScheme(s))
            return true;

        if (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.StartsWith("127.0.0.1"))
            return true;

        var host = s.Split('/')[0];
        return host.Contains('.') && !host.EndsWith('.');
    }

    private static string Fix(string s)
    {
        return HasScheme(s) ? s : "https://" + s;
    }
}
