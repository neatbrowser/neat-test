using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace neat;

/// <summary>
/// The Library, as in Zen: a panel of its own (libpanel in win.xaml), not a mode of the
/// sidebar. Opening it makes the sidebar step aside, whatever state the sidebar is in
/// (docked, hidden or peeking), and slides the panel in from the left edge. The panel has no
/// background, so the window colour and its grain run straight through it.
///
/// The page is pushed to the right, not squeezed: it keeps its width and height, only its
/// position changes, and what no longer fits is cut off by the window's edge. That is done
/// with Translation, which moves the page without a layout pass, so the web view is never
/// resized and the page inside does not reflow while the panel slides.
///
/// Closing it slides everything back, and the sidebar returns to the state it was in. The
/// sidebar's own state (Layout in win.side.cs) is not changed by the Library; Layout just asks
/// LibLayout below what to do about the panel and the page. The shortcuts that open it are in
/// win.keys.cs.
/// </summary>
public sealed partial class Win
{
    // The panel's width and its gap to the window edge come from libpanel in win.xaml, so there
    // is one place to change them. The page starts one window border (6px) after the panel.
    private const double LibGap = 6;

    // How far from the window's left edge the page starts while the Library is open.
    private double LibEdge => libpanel.Margin.Left + libpanel.Width + LibGap;

    private bool _lib;

    // Collapses the panel once it has slid out, so a closed Library takes no focus or input.
    private DispatcherQueueTimer? _libGone;

    // The section the Library opens on when it is opened from the footer button: the last one used.
    private Sec _sec = Sec.History;

    private void LibInit()
    {
        lib.Back += LibShut;
        lib.Opened += url => _ = Open(url);

        // The dot on the button tells whether a download is going (win.dl.cs).
        App.Dl.ActiveChanged += Dot;
        Dot();

        // A bookmark or history entry went: the star in the address bar and the footer counters depend on them.
        lib.Edited += () =>
        {
            _ = Star();
            _ = Stats();
        };

        // The panel waits outside the left edge. This is set before the animations are switched
        // on, so the window does not play a slide at startup.
        libpanel.Translation = new Vector3((float)-LibEdge, 0, 0);
        libpanel.Opacity = 0;
        libpanel.IsHitTestVisible = false;

        libpanel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Fade) };
        libpanel.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Slide) };
        frame.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Slide) };

        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(Slide + 50);
        t.IsRepeating = false;
        t.Tick += (s, e) =>
        {
            t.Stop();
            if (!_lib)
                libpanel.Visibility = Visibility.Collapsed;
        };
        _libGone = t;
    }

    /// <summary>Opens the Library on a section. If it is open already, switches to that section and reloads it.</summary>
    private void ShowLib(Sec sec)
    {
        _sec = sec;

        if (!_lib)
        {
            _lib = true;

            // A peeking sidebar steps aside with the rest; closing the Library leaves it hidden.
            _peek = false;

            _libGone?.Stop();
            libpanel.Visibility = Visibility.Visible;

            // A change made in the same pass that makes the element visible does not animate
            // (it has no visual yet and would simply appear). Layout runs once it has one.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Layout);
        }

        lib.Show(sec);
    }

    /// <summary>Closes the Library: the panel slides out, the page slides back and the sidebar returns.</summary>
    private void LibShut()
    {
        if (!_lib)
            return;

        _lib = false;

        lib.Park();
        Layout();

        // Once it is out of sight it is collapsed, see LibInit.
        _libGone?.Start();
    }

    /// <summary>
    /// The Library's part of Layout(): where the panel and the page are. The sidebar's own part
    /// (it steps aside while the Library is open) is in Layout() itself.
    /// </summary>
    private void LibLayout()
    {
        libpanel.Translation = new Vector3(_lib ? 0 : (float)-LibEdge, 0, 0);
        libpanel.Opacity = _lib ? 1 : 0;
        libpanel.IsHitTestVisible = _lib;

        // The page keeps its layout (so its size does not change) and is moved sideways by the
        // difference between where it starts now (right after a docked sidebar's column, or one
        // border from the edge) and where it should start.
        var at = _dock ? SideW : 6;
        frame.Translation = new Vector3(_lib ? (float)(LibEdge - at) : 0, 0, 0);
    }

    private void libbtn_Click(object sender, RoutedEventArgs e)
    {
        ShowLib(_sec);
    }
}
