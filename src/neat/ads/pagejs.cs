using System.Text.Json;

namespace neat;

/// <summary>
/// Fills in ads/page.js for one host. Pure text work, so the self-check can run it.
/// </summary>
internal static class PageJs
{
    /// <param name="template">The text of ads/page.js.</param>
    /// <param name="host">The host the copy is for, as location.hostname shows it.</param>
    /// <param name="token">The window's secret, so only the app's messages are believed.</param>
    /// <param name="hide">Selectors to hide at once.</param>
    /// <param name="generic">True when the page should also be hidden by class and id rules.</param>
    /// <param name="scriptlets">The engine's injected script, or empty.</param>
    /// <param name="procedural">The engine's procedural rules (JSON texts), or none.</param>
    public static string Build(string template, string host, string token, string[] hide, bool generic, string scriptlets, string[]? procedural = null)
    {
        // The scriptlets go in last: they are the one piece that may contain any text.
        return template
            .Replace("__NEAT_HOST__", JsonSerializer.Serialize(host))
            .Replace("__NEAT_TOKEN__", JsonSerializer.Serialize(token))
            .Replace("__NEAT_HIDE__", JsonSerializer.Serialize(hide))
            .Replace("__NEAT_GENERIC__", generic ? "true" : "false")
            .Replace("__NEAT_PROC__", JsonSerializer.Serialize(procedural ?? Array.Empty<string>()))
            .Replace("/*__NEAT_SCRIPTLETS__*/", scriptlets);
    }
}
