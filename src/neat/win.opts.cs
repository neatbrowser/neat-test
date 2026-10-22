using Microsoft.UI.Xaml;

namespace neat;

/// <summary>
/// Opens the settings window: Ctrl+, (registered in win.keys.cs) or the gear in
/// the sidebar footer. Only one settings window exists at a time.
/// </summary>
public sealed partial class Win
{
    private Opts? _opts;

    /// <summary>
    /// The WebView2 version in use, e.g. "154.0.4258.62", or null until the first
    /// tab has started its web view. The About page reads it from here, so it
    /// does not need a web view of its own.
    /// </summary>
    internal string? WebVer { get; private set; }

    private void ShowOpts()
    {
        // Already open: bring that one forward instead of making a second.
        if (_opts is not null)
        {
            _opts.Activate();
            return;
        }

        var o = new Opts(this);

        // Forget it when it closes, so the next request makes a new one.
        o.Closed += (s, e) => _opts = null;

        _opts = o;
        o.Activate();
    }

    private void gear_Click(object sender, RoutedEventArgs e)
    {
        ShowOpts();
    }
}
