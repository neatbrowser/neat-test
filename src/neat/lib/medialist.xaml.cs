using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The grid under the search row of the Media section. It shows what a page gives it and says
/// what the user did (clicked a tile, asked for its menu, scrolled near the end); the page
/// decides what that means. It asks for each tile's thumbnail when the tile is first shown.
/// </summary>
public sealed partial class MediaList : UserControl
{
    // Tiles from the end of the list at which the page is asked for more.
    private const int Near = 8;

    // True from the moment the page is asked for more until it has been asked. One scroll shows
    // many tiles near the end; this keeps that from adding a chunk for every one of them.
    private bool _asked;

    public MediaList()
    {
        InitializeComponent();
        list.ItemsSource = Items;
    }

    /// <summary>What the grid shows. Tiles can be added and removed one by one without reloading.</summary>
    public ObservableCollection<MediaItem> Items { get; private set; } = new();

    /// <summary>A tile was clicked, or Enter was pressed on it: open it.</summary>
    public event Action<MediaItem>? Picked;

    /// <summary>The menu of a tile was asked for by a right click; the point is inside the tile.</summary>
    public event Action<MediaItem, FrameworkElement, Windows.Foundation.Point>? Asked;

    /// <summary>Tiles near the end of the grid have been shown: the page may add more.</summary>
    public event Action? End;

    /// <summary>A line in the middle of the grid ("No media found"); null hides it.</summary>
    public string? Note
    {
        set
        {
            empty.Text = value ?? string.Empty;
            empty.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Shows a spinning ring while there is nothing to show yet.</summary>
    public bool Busy
    {
        set
        {
            busy.IsActive = value;
            busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Replaces everything in the grid at once (one change for the grid, not one per tile).</summary>
    public void Set(IEnumerable<MediaItem> rows)
    {
        Items = new ObservableCollection<MediaItem>(rows);
        list.ItemsSource = Items;
    }

    /// <summary>
    /// Gives the dim text, the tiles and the hover washes the colours of the current window colour.
    /// internal, not public: Skin is internal, and a public method may not take it.
    /// </summary>
    internal void Paint(Skin s)
    {
        ((SolidColorBrush)Resources["libdim"]).Color = s.Dim.Color;

        // The text colour at some strength, so it shows on a light window too.
        byte ink = s.Dark ? (byte)255 : (byte)0;
        ((SolidColorBrush)Resources["tilewash"]).Color = Windows.UI.Color.FromArgb(0x1F, ink, ink, ink);

        // The same washes as the other lists.
        ((SolidColorBrush)list.Resources["ListViewItemBackgroundPointerOver"]).Color = Windows.UI.Color.FromArgb(0x1A, ink, ink, ink);
        ((SolidColorBrush)list.Resources["ListViewItemBackgroundPressed"]).Color = Windows.UI.Color.FromArgb(0x26, ink, ink, ink);
    }

    private void list_Row(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        // A tile that scrolls away or is reused for another file says so, so the work for
        // its thumbnail can be skipped (see Thumbs.Fill).
        if (e.Item is MediaItem old)
            old.Live = !e.InRecycleQueue;

        if (e.InRecycleQueue)
            return;

        if (e.ItemContainer is ListViewItem box)
        {
            box.MinHeight = 0;
            box.MinWidth = 0;
            box.CornerRadius = new CornerRadius(10);
            box.Margin = new Thickness(3);
            box.Padding = new Thickness(0);
        }

        if (e.Item is MediaItem it)
            _ = Thumbs.Fill(it);

        if (e.ItemIndex >= Items.Count - Near)
            More();
    }

    /// <summary>
    /// Asks the page for more, but not from inside the layout pass that is showing the tiles:
    /// adding tiles there would change the list while it is being measured.
    /// </summary>
    private void More()
    {
        if (_asked)
            return;

        _asked = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            _asked = false;
            End?.Invoke();
        });
    }

    private void list_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem m)
            Picked?.Invoke(m);
    }

    private void tile_Menu(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is MediaItem m)
        {
            e.Handled = true;
            Asked?.Invoke(m, el, e.GetPosition(el));
        }
    }
}
