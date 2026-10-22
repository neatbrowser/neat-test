using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The list under the search row of the Downloads section. It shows what a page gives it
/// and says what the user did (clicked a row, asked for its menu); the page decides what
/// that means. The rows are live: see dllist.xaml.
/// </summary>
public sealed partial class DlList : UserControl
{
    // When a button of a row ("copy link", "more") was last pressed (Environment.TickCount64). See list_ItemClick.
    private long _gone;

    public DlList()
    {
        InitializeComponent();
    }

    /// <summary>A row was clicked.</summary>
    public event Action<Download>? Picked;

    /// <summary>
    /// The menu of a row was asked for, by its "more" button (no position) or by a right click
    /// (the position inside the row, so the menu opens at the pointer).
    /// </summary>
    public event Action<Download, FrameworkElement, Windows.Foundation.Point?>? Asked;

    /// <summary>A line in the middle of the list ("No downloads found"); null hides it.</summary>
    public string? Note
    {
        set
        {
            empty.Text = value ?? string.Empty;
            empty.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>What the list shows. A collection that changes (an ObservableCollection) is followed as it changes.</summary>
    public void Show(IEnumerable<Download> rows)
    {
        list.ItemsSource = rows;
    }

    /// <summary>
    /// Gives the dim text and the hover washes the colours of the current window colour.
    /// internal, not public: Skin is internal, and a public method may not take it.
    /// </summary>
    internal void Paint(Skin s)
    {
        ((SolidColorBrush)Resources["libdim"]).Color = s.Dim.Color;

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
        }
    }

    private void list_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Pressing a button of the row ("copy link", "more") can also arrive here as a click on the row it sits in.
        if (Environment.TickCount64 - _gone < 300)
            return;

        if (e.ClickedItem is Download d)
            Picked?.Invoke(d);
    }

    private void more_Click(object sender, RoutedEventArgs e)
    {
        _gone = Environment.TickCount64;

        if (sender is FrameworkElement el && el.DataContext is Download d)
            Asked?.Invoke(d, el, null);
    }

    /// <summary>
    /// Copies the address the file is downloaded from, and shows a check mark on the button for a
    /// moment. Only the Download is needed, so the list does it itself instead of asking the page.
    /// </summary>
    private async void copy_Click(object sender, RoutedEventArgs e)
    {
        _gone = Environment.TickCount64;

        // An async void handler: an exception that gets out of it ends the whole app.
        try
        {
            if (sender is not Button b || b.DataContext is not Download d)
                return;

            if (!Copier.Text(d.Url))
            {
                await App.Dl.Flash(d, "Could not copy the link");
                return;
            }

            if (b.Content is FontIcon ic)
            {
                ic.Glyph = "";
                await Task.Delay(1200);
                ic.Glyph = "";
            }
        }
        catch (Exception ex)
        {
            Log.Error("library", ex, "copy link");
        }
    }

    private void row_Menu(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Download d)
        {
            e.Handled = true;
            Asked?.Invoke(d, el, e.GetPosition(el));
        }
    }

    private void row_In(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Visible);
    }

    private void row_Out(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Collapsed);
    }

    /// <summary>
    /// Shows or hides the buttons of a row. They sit together in one StackPanel, marked
    /// Tag="tools" in dllist.xaml (the name and status panel of the row is a StackPanel too).
    /// </summary>
    private static void Cross(object sender, Visibility v)
    {
        if (sender is not Grid g)
            return;

        foreach (var child in g.Children)
        {
            if (child is StackPanel p && p.Tag is "tools")
                p.Visibility = v;
        }
    }
}
