using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace neat;

/// <summary>A day heading in a Library list ("Today", "Yesterday", a date).</summary>
public sealed class LibHead
{
    public LibHead(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

/// <summary>
/// One row of a Library list: a page from history or a bookmark. The favicon
/// is only fetched when the row is first shown, so a list of thousands costs
/// nothing for the rows that are never scrolled to.
/// </summary>
public sealed class LibRow : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private readonly string? _iconSrc;
    private bool _tried;

    public LibRow(string url, string? title, string? icon)
    {
        Url = url;
        Title = string.IsNullOrWhiteSpace(title) ? url : title;
        Host = HostOf(url);
        _iconSrc = icon;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Url { get; }

    /// <summary>The page's title, or its address when it has none.</summary>
    public string Title { get; }

    public string Host { get; }

    /// <summary>
    /// The history entries this row stands for. Visits of the same page on the same
    /// day share one row, so deleting the row has to delete all of them. Empty for a bookmark.
    /// </summary>
    public List<int> Ids { get; } = new();

    public ImageSource? Icon
    {
        get
        {
            if (!_tried)
                Load();

            return _icon;
        }
    }

    public Visibility IconVis => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Until the favicon is there (or if it cannot be shown) a globe stands in.</summary>
    public Visibility GlyphVis => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    private void Load()
    {
        _tried = true;

        // Only web addresses: anything else (a data: address, a broken value) is not worth trying.
        if (string.IsNullOrEmpty(_iconSrc)
            || !Uri.TryCreate(_iconSrc, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var img = new BitmapImage { DecodePixelWidth = 32, UriSource = u };

        // Formats the image control cannot decode (SVG, a damaged file) fall back to the globe.
        img.ImageFailed += (s, e) =>
        {
            _icon = null;
            Raise(nameof(Icon));
            Raise(nameof(IconVis));
            Raise(nameof(GlyphVis));
        };

        _icon = img;
    }

    private static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
            return url;

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Picks the template for a Library list: day headings and rows look different.</summary>
public sealed class LibSel : DataTemplateSelector
{
    public DataTemplate? Head { get; set; }

    public DataTemplate? Row { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return (item is LibHead ? Head : Row) ?? new DataTemplate();
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
