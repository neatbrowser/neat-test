using Microsoft.UI.Xaml;

namespace neat;

/// <summary>The Appearance page: the window colour.</summary>
public sealed partial class Opts
{
    // The colour menu while this page is on show. It is told to stop following the colour
    // when the page goes or the window closes.
    private Picker? _pk;

    private UIElement BuildLook()
    {
        // The same control as the sidebar's colour flyout, so the two cannot differ: light or
        // dark, the colour pad, how much of the colour shows, the texture dial and the swatches.
        // It repaints this window too, through App.Look.Changed (see Paint in opts.xaml.cs).
        _pk = new Picker
        {
            // A little wider than in the sidebar (300): the pad, which is two thirds of the
            // width, and the sliders grow with it.
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };

        return Block.Page(
            Block.Head(_skin, "Window colour"),
            Block.Card(_skin, _pk));
    }
}
