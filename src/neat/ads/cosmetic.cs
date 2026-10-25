using System.Text.Json;

namespace neat;

/// <summary>What the engine wants done to one page besides blocking requests.</summary>
internal sealed class Cosmetic
{
    public static Cosmetic Empty { get; } = new();

    /// <summary>CSS selectors to hide on the page.</summary>
    public string[] HideSelectors { get; init; } = Array.Empty<string>();

    /// <summary>Selectors the generic rules must not hide (passed back when asking for generic ones).</summary>
    public string[] Exceptions { get; init; } = Array.Empty<string>();

    /// <summary>Script text of the scriptlets to run before the page's own scripts.</summary>
    public string InjectedScript { get; init; } = string.Empty;

    /// <summary>True when the page asked for no generic hiding.</summary>
    public bool GenericHide { get; init; }

    /// <summary>
    /// The procedural rules (`:has-text`, `:upward`, `:remove()` ...), each one a JSON text as the
    /// engine wrote it. ads/page.js runs them.
    /// </summary>
    public string[] ProceduralActions { get; init; } = Array.Empty<string>();

    /// <summary>How many procedural rules the page has.</summary>
    public int Procedural => ProceduralActions.Length;

    /// <summary>Reads the JSON of neat_cosmetic_resources. A malformed text gives Empty.</summary>
    public static Cosmetic Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            return new Cosmetic
            {
                HideSelectors = Strings(r, "hide_selectors"),
                Exceptions = Strings(r, "exceptions"),
                InjectedScript = r.TryGetProperty("injected_script", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? string.Empty
                    : string.Empty,
                GenericHide = r.TryGetProperty("generichide", out var g) && g.ValueKind == JsonValueKind.True,
                ProceduralActions = Strings(r, "procedural_actions"),
            };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static string[] Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>(a.GetArrayLength());
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } t)
                list.Add(t);
        }

        return list.ToArray();
    }
}
