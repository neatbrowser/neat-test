using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// A section that is not built yet: one dim line in the middle and nothing else. Media
/// and Downloads use it until their real pages replace it (a one-line change in
/// LibView.Pages()).
/// </summary>
internal sealed class SoonPage : LibPage
{
    private readonly string _name;
    private readonly string _glyph;

    private readonly TextBlock _note = new()
    {
        Text = "Coming in a later update",
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public SoonPage(string name, string glyph)
    {
        _name = name;
        _glyph = glyph;
    }

    public override string Name
    {
        get { return _name; }
    }

    public override string Glyph
    {
        get { return _glyph; }
    }

    public override UIElement View
    {
        get { return _note; }
    }

    public override Task Load()
    {
        return Task.CompletedTask;
    }

    public override void Paint(Skin s)
    {
        _note.Foreground = s.Dim;
    }
}
