using System.Reflection;
using Microsoft.UI.Xaml;

namespace neat;

/// <summary>The About page: which build this is and where its data lives.</summary>
public sealed partial class Opts
{
    private UIElement BuildAbout()
    {
        // The browser window reads this from its first web view once that has started.
        // This window makes no web view of its own just to ask.
        var web = _own.WebVer ?? "Not loaded yet";
        var kind = Env.UsesFixed ? "Bundled" : "System";

        return Block.Page(
            Block.Head(_skin, "NEAT"),
            Block.Card(_skin,
                Block.Row(_skin, "Version", null, Block.Value(_skin, AppVer())),
                Block.Row(_skin, "Data folder", "Settings, history, bookmarks and the web profile.", Block.Value(_skin, Env.Root))),
            Block.Head(_skin, "WebView2"),
            Block.Card(_skin,
                Block.Row(_skin, "Version", null, Block.Value(_skin, web)),
                Block.Row(_skin, "Runtime", "Bundled ships inside NEAT; System is the one installed in Windows.", Block.Value(_skin, kind))));
    }

    /// <summary>The version set in neat.csproj, e.g. "0.0.2".</summary>
    private static string AppVer()
    {
        var a = typeof(App).Assembly;

        // The build can append "+commit" to the informational version; it is noise here.
        var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(v))
        {
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }

        return a.GetName().Version?.ToString() ?? "unknown";
    }
}
