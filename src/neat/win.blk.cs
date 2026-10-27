using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace neat;

/// <summary>
/// NEAT Blocker's chip and panel. The chip in the title bar, beside Reload, shows how many requests
/// the blocker has stopped since NEAT started ("Blocked: N"). Clicking it opens a small panel at the
/// page's top-left corner, a few pixels inside it: what was stopped on this page and in total, a
/// switch for the blocker on this site, and which filter lists are in use. The panel lives in the
/// page's column of the window, so it moves with the page when the sidebar is docked or put away
/// (see Layout in win.side.cs, which calls <see cref="BlkPlace"/>).
/// </summary>
public sealed partial class Win
{
    /// <summary>Distance of the panel from the page's top and left edges.</summary>
    private const double BlkGap = 8;

    private DispatcherQueueTimer? _blkTimer;
    private bool _blkOpen;

    // True while the switch is being set from code, so that does not count as the person's click.
    private bool _blkSync;

    private void BlkInit()
    {
        // The counters change from other threads (requests); the chip is read on a timer instead of
        // being told about every request, which would be thousands of UI updates.
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(700);
        t.IsRepeating = true;
        t.Tick += (s, e) => BlkRefresh();
        t.Start();
        _blkTimer = t;

        BlkPlace();
        BlkRefresh();
    }

    private void BlkStop() => _blkTimer?.Stop();

    /// <summary>Puts the panel at the page's corner. The page's column starts after a docked sidebar.</summary>
    private void BlkPlace()
    {
        // The page has a margin on its left only when no sidebar sits beside it; the Library moves
        // the page with a translation, and the panel goes with it.
        blkpanel.Margin = new Thickness(frame.Margin.Left + BlkGap, BlkGap, 0, 0);
        blkpanel.Translation = frame.Translation;
    }

    /// <summary>Opens or closes the panel.</summary>
    private void BlkShow(bool on)
    {
        if (_blkOpen == on)
            return;

        _blkOpen = on;
        blkpanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            BlkPlace();
            BlkRefresh();
        }
    }

    /// <summary>The selected tab's address as the blocker sees it, or null when it is not a web page.</summary>
    private string? BlkHost()
    {
        var src = _cur?.Src;
        return AdUrl.IsWeb(src) ? AdUrl.Host(src!) : null;
    }

    /// <summary>Brings the chip, and the panel when it is open, up to date.</summary>
    private void BlkRefresh()
    {
        if (_closed)
            return;

        var ads = App.Ads;
        var host = BlkHost();
        var off = ads.IsOff(host);

        var text = "Blocked: " + ads.Blocked.ToString("N0", CultureInfo.CurrentCulture);
        if (blktxt.Text != text)
            blktxt.Text = text;

        var dim = off ? 0.55 : 1.0;
        if (blk.Opacity != dim)
            blk.Opacity = dim;

        if (!_blkOpen)
            return;

        var page = _cur is null ? 0 : AdsOf(_cur).Blocked;
        BlkText(blkpage, page.ToString("N0", CultureInfo.CurrentCulture));
        BlkText(blkall, ads.Blocked.ToString("N0", CultureInfo.CurrentCulture));
        BlkText(blkhost, host is null ? "Not a web page" : Dom(_cur?.Src));

        _blkSync = true;
        try
        {
            blkon.IsEnabled = ads.On && host is not null;
            if (blkon.IsOn == off)
                blkon.IsOn = !off;
        }
        finally
        {
            _blkSync = false;
        }

        string info;
        if (!ads.On)
            info = "The blocker is switched off in settings.";
        else if (!ads.Ready)
            info = "Starting…";
        else
            info = $"Filters {ads.Version}\n{ads.UpdateStatus}";
        BlkText(blkinfo, info);
    }

    private static void BlkText(Microsoft.UI.Xaml.Controls.TextBlock t, string text)
    {
        if (t.Text != text)
            t.Text = text;
    }

    private void blk_Click(object sender, RoutedEventArgs e) => BlkShow(!_blkOpen);

    private void blkx_Click(object sender, RoutedEventArgs e) => BlkShow(false);

    private void blk_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            BlkShow(false);
        }
    }

    /// <summary>The switch: the blocker on or off for this site, then the page loads again so it takes effect.</summary>
    private void blkon_Toggled(object sender, RoutedEventArgs e)
    {
        if (_blkSync)
            return;

        var host = BlkHost();
        if (host is null)
            return;

        var ads = App.Cfg.Cur.Ads;

        // Every entry that covers this host goes; when the blocker is to be off, the site is listed
        // once, without "www." so it covers the site's other subdomains too.
        var list = ads.Off.Where(o => !AdUrl.Covers(o, host)).ToList();
        if (!blkon.IsOn)
            list.Add(host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host);

        // A new list, not an edit of the old one: requests read it from other threads.
        ads.Off = list;
        App.Cfg.Save();
        Log.Write("ads", $"blocker {(blkon.IsOn ? "on" : "off")} for {host}");

        BlkRefresh();
        _cur?.View.CoreWebView2?.Reload();
    }
}
