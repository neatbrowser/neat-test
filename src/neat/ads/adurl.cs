namespace neat;

/// <summary>Small address helpers for the blocker. No WinUI in here, so the self-check can run them.</summary>
internal static class AdUrl
{
    /// <summary>Only http and https are judged. data:, blob:, file: and the like are never blocked.</summary>
    public static bool IsWeb(string? uri) =>
        uri is not null
        && (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The host as a script sees it in location.hostname (lower case, ASCII/punycode), or null
    /// when the address is not an http(s) address.
    /// </summary>
    public static string? Host(string? url)
    {
        if (!IsWeb(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
            return null;

        return u.IdnHost.ToLowerInvariant();
    }

    /// <summary>True when the entry <paramref name="off"/> of the person's "blocker off" list is this host or a parent of it.</summary>
    public static bool Covers(string off, string host) =>
        off.Length > 0
        && (host.Equals(off, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + off, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address without its #fragment, for comparing two addresses of one navigation.</summary>
    public static string NoHash(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        var i = url.IndexOf('#');
        return i < 0 ? url : url[..i];
    }
}
