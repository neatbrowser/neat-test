using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The list under the search row of History and Bookmarks: rows (LibRow) and day
/// headings (LibHead). It holds no data of its own; a page puts rows into Items
/// and listens to the events below.
/// </summary>
public sealed partial class LibList : UserControl
{
    // When the X of a row was last pressed (Environment.TickCount64). See list_ItemClick.
    private long _gone;

    public LibList()
    {
        InitializeComponent();
        list.ItemsSource = Items;
    }

    /// <summary>What the list shows. Rows can be added and removed one by one without reloading.</summary>
    public ObservableCollection<object> Items { get; private set; } = new();

    /// <summary>A row was clicked: open it.</summary>
    public event Action<LibRow>? Picked;

    /// <summary>The X of a row was pressed: remove it.</summary>
    public event Action<LibRow>? Dropped;

    /// <summary>Rows near the end of the list have been shown: the page may add more.</summary>
    public event Action? End;

    /// <summary>
    /// A line in the middle of the list ("No history found"); null hides it. Set it
    /// when the list is empty and clear it when it is not.
    /// </summary>
    public string? Note
    {
        set
        {
            empty.Text = value ?? string.Empty;
            empty.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Replaces everything in the list at once (one change for the list, not one per row).</summary>
    public void Set(IEnumerable<object> rows)
    {
        Items = new ObservableCollection<object>(rows);
        list.ItemsSource = Items;
    }

    /// <summary>
    /// Gives the dim text and the hover washes the colours of the current window colour.
    /// internal, not public: Skin is internal, and a public method may not take it.
    /// </summary>
    internal void Paint(Skin s)
    {
        ((SolidColorBrush)Resources["libdim"]).Color = s.Dim.Color;

        // The same washes as the tab list in win.xaml: faint on hover, stronger while pressed.
        // In the text colour, so they show on a light window too.
        byte ink = s.Dark ? (byte)255 : (byte)0;
        ((SolidColorBrush)list.Resources["ListViewItemBackgroundPointerOver"]).Color = Windows.UI.Color.FromArgb(0x1A, ink, ink, ink);
        ((SolidColorBrush)list.Resources["ListViewItemBackgroundPressed"]).Color = Windows.UI.Color.FromArgb(0x26, ink, ink, ink);
    }

    private void list_Row(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.InRecycleQueue)
            return;

        if (e.ItemContainer is ListViewItem box)
        {
            box.MinHeight = 0;
            box.CornerRadius = new CornerRadius(8);
            box.Margin = new Thickness(0, 1, 0, 1);
            box.Padding = new Thickness(8, 0, 4, 0);

            // A day heading is a label, not a row: no hover wash, no click.
            box.IsHitTestVisible = e.Item is LibRow;
        }

        if (e.ItemIndex >= Items.Count - 5)
            End?.Invoke();
    }

    private void list_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Pressing the X can also arrive here as a click on the row it sits in; the row
        // is being removed, so it must not be opened as well.
        if (Environment.TickCount64 - _gone < 300)
            return;

        if (e.ClickedItem is LibRow r)
            Picked?.Invoke(r);
    }

    private void del_Click(object sender, RoutedEventArgs e)
    {
        _gone = Environment.TickCount64;

        if (sender is FrameworkElement el && el.DataContext is LibRow r)
            Dropped?.Invoke(r);
    }

    private void row_In(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Visible);
    }

    private void row_Out(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Collapsed);
    }

    /// <summary>Shows or hides the X of a row. It is the only Button in the row.</summary>
    private static void Cross(object sender, Visibility v)
    {
        if (sender is not Grid g)
            return;

        foreach (var child in g.Children)
        {
            if (child is Button b)
                b.Visibility = v;
        }
    }
}
