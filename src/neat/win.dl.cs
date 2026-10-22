using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Downloads, from the window's side. Hook() hands every download of a tab to OnDownload, which
/// passes it to the engine (App.Dl, data/downloads.cs). Here is only what concerns tabs: a tab
/// that was opened just for a download is taken off the tab list, and the Library button
/// shows a dot while a download runs.
/// </summary>
public sealed partial class Win
{
    // Web views of tabs that were opened only for a download and then taken off the tab list.
    // They are closed when their download is over.
    private readonly List<Tab> _ghosts = new();

    // The dot on the Library button. A colour of its own, so no theme resource is needed.
    private readonly SolidColorBrush _dot = new(ColorHelper.FromArgb(255, 0x4C, 0xC2, 0xFF));

    /// <summary>A tab's web view starts a download.</summary>
    private void OnDownload(Tab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        // An exception that gets out of this handler ends the app.
        try
        {
            var d = App.Dl.Start(e);
            if (d is null)
                return;

            // A link that opens in a new tab (target=_blank, Ctrl+click, "Retry" in the Library)
            // and turns out to be a download leaves an empty tab behind. Browsers close that
            // tab; so does this. Never the last one, and never one that shows a page.
            if (_tabs.Count > 1 && Blank(tab))
                Ghost(tab, d);
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "download start");
        }
    }

    /// <summary>A tab that has never shown anything: no page, nothing to go back to.</summary>
    private static bool Blank(Tab tab)
    {
        var core = tab.View.CoreWebView2;
        if (core is null)
            return false;

        var src = core.Source;
        return !core.CanGoBack && (string.IsNullOrEmpty(src) || src == "about:blank");
    }

    /// <summary>
    /// Takes a tab off the tab list like Shut() does, but keeps its web view alive, hidden, until
    /// the download is over: closing a web view may stop the downloads that belong to it.
    /// </summary>
    private void Ghost(Tab tab, Download d)
    {
        // Not inside the web view's own event: the tab is taken apart a moment later.
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var i = _tabs.IndexOf(tab);
                if (i < 0 || _tabs.Count < 2)
                    return;

                tab.View.Visibility = Visibility.Collapsed;
                _tabs.RemoveAt(i);
                Log.Tabs = _tabs.Count;
                Log.Write("tab", $"close (only a download), tabs now {_tabs.Count}");
                _ghosts.Add(tab);

                if (tab == _cur)
                    Pick(_tabs[Math.Min(i, _tabs.Count - 1)]);

                void Watch(object? s, PropertyChangedEventArgs a)
                {
                    if (a.PropertyName != nameof(Download.State) || d.State is DlState.Downloading or DlState.Paused)
                        return;

                    d.PropertyChanged -= Watch;
                    Reap(tab);
                }

                d.PropertyChanged += Watch;

                // It may already be over (a very small file).
                if (d.State is not (DlState.Downloading or DlState.Paused))
                {
                    d.PropertyChanged -= Watch;
                    Reap(tab);
                }
            }
            catch (Exception ex)
            {
                Log.Error("dl", ex, "closing a tab that was only a download");
            }
        });
    }

    /// <summary>Closes the web view of a tab that was kept for its download.</summary>
    private void Reap(Tab tab)
    {
        try
        {
            if (!_ghosts.Remove(tab))
                return;

            host.Children.Remove(tab.View);
            tab.View.Close();
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "closing the web view of a finished download");
        }
    }

    /// <summary>Shows or hides the dot on the Library button: there is one while any download is going.</summary>
    private void Dot()
    {
        libdot.Fill = _dot;
        libdot.Visibility = App.Dl.Active > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
