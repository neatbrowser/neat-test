using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace neat;

/// <summary>
/// One browser tab: its own web view, plus what the sidebar list shows for it.
/// The list binds to Title, Icon, IconVis and GlyphVis, and refreshes itself
/// whenever they change.
/// </summary>
public sealed class Tab : INotifyPropertyChanged
{
    private string _title = "New Tab";
    private string _src = string.Empty;
    private ImageSource? _icon;

    public Tab(WebView2 view)
    {
        View = view;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WebView2 View { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;

            _title = value;
            Raise();
        }
    }

    /// <summary>Current address of the page.</summary>
    public string Src
    {
        get => _src;
        set
        {
            if (_src == value)
                return;

            _src = value;
            Raise();
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            Raise();
            Raise(nameof(IconVis));
            Raise(nameof(GlyphVis));
        }
    }

    /// <summary>The favicon image shows only once we have one.</summary>
    public Visibility IconVis => _icon is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Until then (or if it fails to load) a globe glyph stands in.</summary>
    public Visibility GlyphVis => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The address the tab was asked to open. It stays until the page reports its own (Src), so a
    /// tab that is still starting can already be kept in the session (win.sess.cs).
    /// </summary>
    public string? Asked { get; set; }

    /// <summary>Address of the favicon, kept for history and bookmarks.</summary>
    public string? IconSrc { get; private set; }

    /// <summary>Loads the favicon from the address WebView2 reports for the page.</summary>
    public void SetIcon(string? uri)
    {
        IconSrc = string.IsNullOrEmpty(uri) ? null : uri;

        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
        {
            Icon = null;
            return;
        }

        var img = new BitmapImage(u);

        // Formats the image control cannot decode (e.g. SVG) fall back to the glyph.
        img.ImageFailed += (s, e) => Icon = null;

        Icon = img;
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
