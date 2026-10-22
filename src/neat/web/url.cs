namespace neat;

/// <summary>Turns whatever was typed in the address box into something navigable.</summary>
internal static class Url
{
    // Temporary: a fixed search engine until the settings service is ported.
    private const string Search = "https://www.google.com/search?q=";

    public static string? Fix(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;

        // No spaces and a dot (or localhost) looks like an address.
        var host = !s.Contains(' ') &&
            (s.Contains('.') || s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        if (host)
            return "https://" + s;

        return Search + Uri.EscapeDataString(s);
    }
}
