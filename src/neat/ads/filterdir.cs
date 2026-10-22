using System.Security.Cryptography;
using System.Text.Json;

namespace neat;

/// <summary>
/// Checks a folder holding one neat-filters release (manifest.json and the files it lists)
/// before the engine reads it. No WinUI in here, so the self-check can run it.
/// </summary>
internal static class FilterDir
{
    /// <summary>
    /// Returns the release's version. Throws InvalidDataException when the folder cannot be
    /// trusted: no manifest, a listed file missing, a hash that does not match, or no list at all.
    /// </summary>
    public static string Verify(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path))
            throw new InvalidDataException("manifest.json is missing in " + dir);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("manifest.json lists no files");

        var lists = 0;
        foreach (var f in files.EnumerateArray())
        {
            var name = Text(f, "name");
            var want = Text(f, "sha256");

            // A name with a folder part would let a manifest point outside its own folder.
            if (name.Length == 0 || name != Path.GetFileName(name))
                throw new InvalidDataException("bad file name in manifest: " + name);

            var file = Path.Combine(dir, name);
            if (!File.Exists(file))
                throw new InvalidDataException(name + " is listed but missing");

            using (var s = File.OpenRead(file))
            {
                var got = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
                if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(name + " does not match its hash");
            }

            if (Text(f, "kind") == "list")
                lists++;
        }

        if (lists == 0)
            throw new InvalidDataException("the release holds no filter list");

        return version;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
