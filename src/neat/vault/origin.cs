namespace neat;

/// <summary>
/// The "site" a password belongs to: scheme, host and port, nothing else. A saved password is
/// only ever offered to a page whose origin is exactly equal, so this has to be strict and
/// has to give the same text for the same site however the address was written.
/// </summary>
public static class Origin
{
    /// <summary>
    /// The origin of a web address, or null when it is not an http or https address.
    /// The host is lower-case and in its ASCII (punycode) form, so a look-alike made of foreign
    /// letters never equals the real site. The default port is left out; the path, query and
    /// any user name in the address are dropped. http and https are different origins.
    /// </summary>
    public static string? Of(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
            return null;

        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            return null;

        // "example.com." and "example.com" are the same site.
        var host = u.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
            return null;

        if (u.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('['))
            host = "[" + host + "]";

        var port = u.IsDefaultPort ? string.Empty : ":" + u.Port;
        return u.Scheme + "://" + host + port;
    }
}
