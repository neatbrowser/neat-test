namespace neat;

/// <summary>
/// Tells WebView2 where to find its runtime and where to keep the browsing
/// profile. Call <see cref="Configure"/> once at startup, before any WebView2
/// control exists.
///
/// Release builds ship a Fixed Version WebView2 runtime in a "WebView2" folder
/// next to neat.exe (copied there by the WebView2.Runtime.X64 NuGet package).
/// When that folder exists we use it, so the app never depends on the
/// system-wide Evergreen runtime. When it does not exist, WebView2 falls back
/// to whatever Evergreen runtime is installed (a dev-time safety net).
///
/// The paths go through WebView2's documented environment variables. A later
/// phase will create a proper CoreWebView2Environment (needed for extensions
/// and the custom neat:// scheme) and replace this.
/// </summary>
internal static class Env
{
    /// <summary>Folder holding the bundled runtime, or null when not bundled.</summary>
    public static string? Fixed { get; private set; }

    /// <summary>Per-user folder for everything the app saves (settings, database, profile).</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEAT");

    /// <summary>Folder where WebView2 keeps cookies, cache and other profile data.</summary>
    public static string Data { get; private set; } = string.Empty;

    public static bool UsesFixed => Fixed is not null;

    public static void Configure()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "WebView2");
        if (Directory.Exists(dir))
        {
            Fixed = dir;
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", dir);
        }

        // Per-user and always writable, even when the app is installed under
        // Program Files (WebView2's default would be next to the .exe).
        Data = Path.Combine(Root, "WebView2Profile");

        Directory.CreateDirectory(Data);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Data);
    }
}
