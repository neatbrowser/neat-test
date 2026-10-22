using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace neat;

/// <summary>
/// Full screen (F11). The window takes the whole screen and the title bar steps aside, so the
/// page gets all the room. F11 again brings everything back.
/// </summary>
public sealed partial class Win
{
    private bool _full;

    // Whether the window was maximized before it went full screen, so it comes back that way.
    private bool _wasMax;

    private void ToggleFull()
    {
        // AppWindow is gone once the window has closed.
        if (_closed)
            return;

        if (!_full)
        {
            _wasMax = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

            if (_wasMax && AppWindow.Presenter is OverlappedPresenter p)
                p.Maximize();
        }

        _full = !_full;

        // The bar's buttons are carved out of the drag area (Holes in win.bar.cs); once the bar is
        // collapsed they have no size and are left out, so nothing else needs to change there.
        bar.Visibility = _full ? Visibility.Collapsed : Visibility.Visible;
    }
}
