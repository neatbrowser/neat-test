using Microsoft.UI.Dispatching;

namespace neat;

/// <summary>
/// The session. Two things are remembered here:
///
///   - the tabs that are open, kept in session.json (data/session.cs) so the next start can
///     open them again when Settings > General > On startup says "Continue where you left off";
///   - the tabs closed lately, which Ctrl+Shift+T brings back, last closed first.
///
/// Only the address of a tab is kept, not its back and forward history.
/// </summary>
public sealed partial class Win
{
    // How many closed tabs are remembered.
    private const int KeepGone = 25;

    // How long the tabs have to stay as they are before they are written to disk.
    private static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(2);

    // Closed tabs, the last closed at the end: its address and where it stood in the list.
    private readonly List<(string Url, int At)> _gone = new();

    private DispatcherQueueTimer? _sessTimer;

    // ---- startup ----

    /// <summary>
    /// Opens the tabs the browser starts with: last time's when settings say so and there are
    /// some, otherwise the home page. Returns the tab that is selected.
    /// </summary>
    private Task<Tab> StartTabs()
    {
        var snap = App.Cfg.Cur.Start == StartMode.Last ? App.Sess.Load() : new Snap();
        if (snap.Urls.Count == 0)
            return Open(App.Cfg.Cur.Home);

        // Every tab is started at once. Open puts its tab in the list before it first waits,
        // so the tabs line up in the order they were saved while their pages load.
        var opening = snap.Urls.Select(u => Open(u)).ToList();

        Pick(_tabs[snap.Active]);
        return opening[snap.Active];
    }

    // ---- keeping the open tabs ----

    /// <summary>
    /// Something about the tabs changed: write them down once they have been still for a moment.
    /// Many changes in a row (a page that redirects, a burst of tabs opening) cost one write.
    /// </summary>
    private void SessionDirty()
    {
        if (_closed)
            return;

        _sessTimer ??= MakeSessTimer();

        // Stop and start again, so the wait begins from the latest change.
        _sessTimer.Stop();
        _sessTimer.Start();
    }

    private DispatcherQueueTimer MakeSessTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = SessionWait;
        t.IsRepeating = false;
        t.Tick += (s, e) => SaveSession();
        return t;
    }

    /// <summary>Writes the open tabs to session.json. Also runs when the window closes.</summary>
    private void SaveSession()
    {
        _sessTimer?.Stop();

        var snap = new Snap();
        foreach (var t in _tabs)
        {
            var url = Addr(t);
            if (url is null)
                continue;

            if (t == _cur)
                snap.Active = snap.Urls.Count;

            snap.Urls.Add(url);
        }

        // Nothing worth keeping (no tab is on a web page): leave what was saved before as it is.
        if (snap.Urls.Count == 0)
            return;

        App.Sess.Save(snap);
    }

    // ---- closed tabs ----

    /// <summary>Notes a tab that is about to be closed, with its place in the list.</summary>
    private void Remember(Tab tab, int at)
    {
        var url = Addr(tab);
        if (url is null)
            return;

        _gone.Add((url, at));

        if (_gone.Count > KeepGone)
            _gone.RemoveAt(0);
    }

    /// <summary>Ctrl+Shift+T: opens the tab that was closed last, back where it stood.</summary>
    private void Reopen()
    {
        if (_gone.Count == 0)
            return;

        var g = _gone[^1];
        _gone.RemoveAt(_gone.Count - 1);

        _ = Open(g.Url, Math.Min(g.At, _tabs.Count));
    }

    // ---- helpers ----

    /// <summary>
    /// The web address of a tab, or null when it is not on a web page. A tab that is still
    /// starting has no address of its own yet, so the one it was asked to open stands in.
    /// </summary>
    private static string? Addr(Tab tab)
    {
        var url = string.IsNullOrEmpty(tab.Src) ? tab.Asked : tab.Src;

        var web = url is not null
            && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        return web ? url : null;
    }
}
