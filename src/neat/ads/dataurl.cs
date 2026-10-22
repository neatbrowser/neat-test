using System.Text;

namespace neat;

/// <summary>
/// Reads a data: address ("data:[type][;base64],data"). The engine hands over its replacement
/// resources (a blank script, a 1x1 gif, ...) in this form.
/// </summary>
internal static class DataUrl
{
    public static bool TryParse(string? url, out string mime, out byte[] body)
    {
        mime = "text/plain";
        body = Array.Empty<byte>();

        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = url.IndexOf(',');
        if (comma < 0)
            return false;

        var meta = url[5..comma].Split(';');
        var data = url[(comma + 1)..];

        var base64 = false;
        for (var i = 1; i < meta.Length; i++)
        {
            if (meta[i].Equals("base64", StringComparison.OrdinalIgnoreCase))
                base64 = true;
        }

        if (meta[0].Length > 0)
            mime = meta[0];

        try
        {
            var raw = Uri.UnescapeDataString(data);
            body = base64 ? Convert.FromBase64String(raw) : Encoding.UTF8.GetBytes(raw);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
