using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace neat;

/// <summary>The kinds of file the Media section shows. The order is the order of the type filter (after "All").</summary>
public enum MediaKind
{
    Image,
    Video,
    Audio,
    Doc,
}

/// <summary>
/// One file in the Media section. The scan makes these on a background thread, so the
/// constructor touches no interface object; the thumbnail brush is only made later, on the
/// UI thread, when the tile is first shown (see <see cref="Thumbs"/>).
/// </summary>
public sealed class MediaItem : INotifyPropertyChanged
{
    private Brush? _fill;

    public MediaItem(string full, string name, MediaKind kind, int folder, DateTime at, bool cloud)
    {
        Full = full;
        Name = name;
        Kind = kind;
        Folder = folder;
        At = at;
        Cloud = cloud;
        Glyph = GlyphOf(kind);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The whole path of the file.</summary>
    public string Full { get; }

    /// <summary>The file name, with its extension. It is the caption of the tile and what the search looks at.</summary>
    public string Name { get; }

    public MediaKind Kind { get; }

    /// <summary>Which folder it was found in: 1 Downloads, 2 Pictures, 3 Documents.</summary>
    public int Folder { get; }

    /// <summary>Last write time (UTC). The list is sorted by it, newest first.</summary>
    public DateTime At { get; }

    /// <summary>
    /// True for a file that lives only in the cloud (OneDrive and the like). Asking Windows for its
    /// thumbnail could make it download the file, so such a file only ever gets its glyph.
    /// </summary>
    public bool Cloud { get; }

    /// <summary>The icon that stands in until there is a thumbnail, or for good when there cannot be one.</summary>
    public string Glyph { get; }

    /// <summary>
    /// The thumbnail as a brush, so it is drawn inside the rounded corners of its border. Null until
    /// there is one. The border is ThumbW by ThumbH, the picture's own shape, so nothing is cropped.
    /// </summary>
    public Brush? Fill => _fill;

    /// <summary>Size of the picture inside its cell: as large as fits, with the shape it has. 0 until there is a thumbnail.</summary>
    public double ThumbW { get; private set; }

    public double ThumbH { get; private set; }

    /// <summary>The picture shows only once there is one; until then (or for good, if there cannot be one) the glyph does.</summary>
    public Visibility ThumbVis => _fill is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVis => _fill is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A thumbnail was asked for already (or the file can never have one). Used by <see cref="Thumbs"/> only.</summary>
    internal bool Asked { get; set; }

    /// <summary>A tile is showing this item right now. Used by <see cref="Thumbs"/> only: work for a tile that has been recycled is skipped.</summary>
    internal bool Live { get; set; }

    // The size of a cell of the grid (medialist.xaml: the tile is 140 by 120). A picture is fitted into it.
    private const double CellW = 140;
    private const double CellH = 120;

    /// <summary>Gives the tile its thumbnail, at the picture's own shape. UI thread only.</summary>
    internal void Show(BitmapImage img)
    {
        double w = img.PixelWidth;
        double h = img.PixelHeight;

        // A picture with no size cannot be drawn; the glyph stays.
        if (w <= 0 || h <= 0)
            return;

        // As large as fits in the cell without changing the shape: a wide picture fills the width,
        // a tall one the height, as Zen shows them. Nothing is cropped to a square.
        var k = Math.Min(CellW / w, CellH / h);
        ThumbW = Math.Round(w * k);
        ThumbH = Math.Round(h * k);

        _fill = new ImageBrush { ImageSource = img, Stretch = Stretch.Fill };
        RaiseThumb();
    }

    /// <summary>The thumbnail was pushed out of the memory cache: back to the glyph, and ask again if the tile is shown again.</summary>
    internal void Drop()
    {
        _fill = null;
        ThumbW = 0;
        ThumbH = 0;
        Asked = false;
        RaiseThumb();
    }

    private void RaiseThumb()
    {
        Raise(nameof(Fill));
        Raise(nameof(ThumbW));
        Raise(nameof(ThumbH));
        Raise(nameof(ThumbVis));
        Raise(nameof(GlyphVis));
    }

    /// <summary>
    /// A glyph for the kind of file: Picture, Video, Audio and Document, the same ones the
    /// Downloads list uses. All four are in both Segoe Fluent Icons and Segoe MDL2 Assets.
    /// </summary>
    private static string GlyphOf(MediaKind kind)
    {
        return kind switch
        {
            MediaKind.Image => "",
            MediaKind.Video => "",
            MediaKind.Audio => "",
            _ => "",
        };
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Thumbnails for the Media section. A tile asks for its picture when it is first shown, so
/// a folder of thousands of files costs nothing for the tiles that are never scrolled to.
///
/// Fill is called from the tile callback of the list, which runs inside a layout pass, and it
/// starts running right there. The calls to Windows (opening the file, making the picture) are
/// therefore made on a pool thread, see Make: a WinRT call such as StorageFile.GetFileFromPathAsync
/// can wait for another apartment, and a wait on the UI thread keeps handling window messages,
/// which XAML does not allow inside a layout pass (it ends the app). The rest, the bookkeeping and
/// building the BitmapImage, runs on the UI thread. A few requests run at a time, and a request
/// whose tile has been recycled by scrolling is dropped before it is sent.
/// </summary>
internal static class Thumbs
{
    // Pictures kept in memory (about 160 KB each at this size).
    private const int Keep = 200;

    // Size asked of Windows, in pixels. The tile is 120 DIPs; this leaves room for a high-DPI screen.
    private const uint Size = 200;

    // Requests to Windows at the same time.
    private const int Lanes = 4;

    private sealed class Hit
    {
        public Hit(string full, DateTime at, BitmapImage img, MediaItem owner)
        {
            Full = full;
            At = at;
            Img = img;
            Owner = owner;
        }

        public string Full { get; }

        public DateTime At { get; }

        public BitmapImage Img { get; }

        // The item showing this picture. When the picture is pushed out, the item goes back to its glyph.
        public MediaItem Owner { get; set; }
    }

    // Most recently used first. The map finds an entry by path; the list knows which one is the oldest.
    private static readonly Dictionary<string, LinkedListNode<Hit>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Hit> Order = new();
    private static readonly SemaphoreSlim Gate = new(Lanes);

    // Counts the times the Library was closed. A request that waited through a closing is dropped.
    private static int _era;

    /// <summary>The Library closed: requests that are still waiting for their turn are dropped.</summary>
    public static void Stop()
    {
        _era++;
    }

    /// <summary>Gives the item its thumbnail: from memory if it is there, else from Windows. Never throws.</summary>
    public static async Task Fill(MediaItem it)
    {
        try
        {
            // Cloud-only files must not be asked for a thumbnail: Windows could download them to make one.
            if (it.Asked || it.Cloud)
                return;

            it.Asked = true;

            if (Map.TryGetValue(it.Full, out var node) && node.Value.At == it.At)
            {
                Order.Remove(node);
                Order.AddFirst(node);
                node.Value.Owner = it;
                it.Show(node.Value.Img);
                return;
            }

            var era = _era;
            await Gate.WaitAsync();

            try
            {
                // While it waited, the tile may have gone off screen or the Library may have closed.
                // Asked goes back to false so the next time the tile is shown it asks again.
                if (era != _era || !it.Live)
                {
                    it.Asked = false;
                    return;
                }

                var img = await Make(it.Full);
                if (img is null)
                    return;

                Remember(it, img);
                it.Show(img);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Write("media", "thumbnail failed for " + it.Name + ": " + ex.Message);
        }
    }

    /// <summary>Asks Windows for the thumbnail. Null when the file has none (Windows then offers a generic icon, which is not wanted).</summary>
    private static async Task<BitmapImage?> Make(string full)
    {
        try
        {
            // Windows is asked from a pool thread, not from here. GetFileFromPathAsync makes a
            // call into another apartment and waits for it; on the UI thread that wait handles
            // window messages, and when this runs inside the list's layout pass XAML finds a
            // second message being processed and ends the app (crash of 18 Oct: 0x8000ffff in
            // CXcpDispatcher::OnReentrancyProtectedWindowMessage). The picture itself is made
            // below, back on the UI thread, because a BitmapImage belongs to it.
            using var th = await Task.Run(async () =>
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(full);

                return await file.GetThumbnailAsync(
                    Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                    Size,
                    Windows.Storage.FileProperties.ThumbnailOptions.None);
            });

            if (th is null || th.Type != Windows.Storage.FileProperties.ThumbnailType.Image)
                return null;

            var img = new BitmapImage();
            await img.SetSourceAsync(th);
            return img;
        }
        catch (Exception ex)
        {
            // A file that was moved, a format with no thumbnail handler: the glyph stays.
            Log.Write("media", "no thumbnail for " + full + ": " + ex.Message);
            return null;
        }
    }

    private static void Remember(MediaItem it, BitmapImage img)
    {
        if (Map.TryGetValue(it.Full, out var old))
        {
            Order.Remove(old);
            Map.Remove(it.Full);
        }

        Map[it.Full] = Order.AddFirst(new Hit(it.Full, it.At, img, it));

        while (Order.Count > Keep && Order.Last is { } last)
        {
            Order.RemoveLast();
            Map.Remove(last.Value.Full);
            last.Value.Owner.Drop();
        }
    }
}
