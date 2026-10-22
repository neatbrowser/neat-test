using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace neat;

/// <summary>
/// The title bar: logo, sidebar toggle, back / forward / reload, and the
/// address chip (copy link, domain, bookmark star). The system still draws
/// minimize / maximize / close at the right end, so Windows 11 snap layouts
/// keep working.
/// </summary>
public sealed partial class Win
{
    // Last click-through regions handed to the system, so identical ones are not sent again.
    private RectInt32[] _holes = Array.Empty<RectInt32>();

    // Set once the window has closed. After that AppWindow is no longer usable.
    private bool _closed;

    private void BarInit()
    {
        // The content reaches the top edge and the whole bar is the drag area.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(bar);

        // System caption buttons stay, made transparent so they sit on our background.
        var t = AppWindow.TitleBar;
        t.ButtonBackgroundColor = Colors.Transparent;
        t.ButtonInactiveBackgroundColor = Colors.Transparent;

        // Buttons inside a draggable bar only receive clicks where the system
        // has been told to pass them through (see Holes).
        // A named handler, so OnClosed can take it off again (see _closed).
        bar.LayoutUpdated += BarLayout;

        Accel(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Area.Page, "Copy link", () => _ = CopyLink());

        // Same keys as Zen: Ctrl+T new tab; Ctrl+L, Alt+D, Ctrl+K and Ctrl+E edit the address.
        Accel(VirtualKey.T, VirtualKeyModifiers.Control, Area.Tabs, "New tab", () => ShowBar(false));
        Accel(VirtualKey.L, VirtualKeyModifiers.Control, Area.Navigation, "Search or enter address", () => ShowBar(true));
        Accel(VirtualKey.D, VirtualKeyModifiers.Menu, Area.Navigation, "Search or enter address", () => ShowBar(true));
        Accel(VirtualKey.K, VirtualKeyModifiers.Control, Area.Navigation, "Search or enter address", () => ShowBar(true));
        Accel(VirtualKey.E, VirtualKeyModifiers.Control, Area.Navigation, "Search or enter address", () => ShowBar(true));
    }

    private void BarLayout(object? sender, object e)
    {
        Holes();
    }

    /// <summary>
    /// Tells the system which rectangles of the title bar are buttons rather
    /// than window-drag area. Positions come from the buttons themselves and are
    /// scaled to physical pixels. Runs after every layout, but only talks to the
    /// system when something actually moved.
    /// </summary>
    private void Holes()
    {
        // Layout is still reported while the window closes, when AppWindow is
        // already gone and using it would throw.
        if (_closed || root.XamlRoot is null)
            return;

        var k = root.XamlRoot.RasterizationScale;
        var all = new List<RectInt32>();

        foreach (var b in new FrameworkElement[] { tog, back, fwd, reload, copy, domb, star })
        {
            if (b.ActualWidth <= 0 || b.ActualHeight <= 0)
                continue;

            // Position in window coordinates; null means "relative to the window content".
            var r = b.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));

            all.Add(new RectInt32(
                (int)Math.Round(r.X * k),
                (int)Math.Round(r.Y * k),
                (int)Math.Round(r.Width * k),
                (int)Math.Round(r.Height * k)));
        }

        var now = all.ToArray();
        if (now.SequenceEqual(_holes))
            return;

        _holes = now;
        Microsoft.UI.Input.InputNonClientPointerSource
            .GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, now);
    }

    /// <summary>Shows the selected tab's site in the address chip.</summary>
    private void ShowDom(Tab tab)
    {
        dom.Text = Dom(tab.Src);
        ToolTipService.SetToolTip(domb, string.IsNullOrEmpty(tab.Src) ? "Search or enter address (Ctrl+L)" : tab.Src);
    }

    /// <summary>"https://www.example.com/a/b" becomes "example.com"; anything else is shown as it is.</summary>
    private static string Dom(string? src)
    {
        if (string.IsNullOrEmpty(src))
            return "New Tab";

        var web = Uri.TryCreate(src, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        if (!web)
            return src;

        var h = u!.Host;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }

    private void domb_Click(object sender, RoutedEventArgs e)
    {
        ShowBar(true);
    }

    private async void copy_Click(object sender, RoutedEventArgs e)
    {
        await CopyLink();
    }

    /// <summary>Copies the selected tab's address, and shows a check mark for a moment.</summary>
    private async Task CopyLink()
    {
        var url = _cur?.Src;
        if (string.IsNullOrEmpty(url))
            return;

        // The clipboard can be held by another program for an instant (see Copier.Text).
        if (!Copier.Text(url))
            return;

        copyic.Glyph = "\uE73E";
        await Task.Delay(1200);
        copyic.Glyph = "\uE71B";
    }
}
