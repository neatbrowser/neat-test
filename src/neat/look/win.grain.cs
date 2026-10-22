using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace neat;

/// <summary>
/// The texture of the window colour, as in Zen Browser. A layer of fine noise
/// lies over the window's colour, under the title bar and the sidebar, and
/// the dial in the colour menu sets how strongly it shows. The page area is not
/// affected: it is covered by the web view anyway. The dial itself is in
/// picker.dial.cs. The maths is in <see cref="Grain"/>.
///
/// The noise is one small tile made when it is first needed and repeated over
/// the window as many times as it takes (a window has no way to repeat an image
/// by itself).
/// </summary>
public sealed partial class Win
{
    private byte[]? _tileBytes;        // the noise tile's pixels, made once
    private WriteableBitmap? _tile;     // the tile as a bitmap, for the window's layer
    private int _sideRows;              // how many tiles tall the floating sidebar's strip is (0: none yet)

    private void GrainInit()
    {
        // The tiles have to cover the window, so they follow its size.
        root.SizeChanged += (s, e) =>
        {
            if (App.Look.Cur.Texture > 0)
            {
                Tiles();
                SideGrain();
            }
        };
    }

    /// <summary>Brings the grain layer up to date with the current texture.</summary>
    private void GrainPaint()
    {
        var t = App.Look.Cur;
        var on = t.Texture > 0;

        grain.Opacity = Grain.Opacity(t.Texture);
        grain.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
            Tiles();
        SideGrain();
    }

    // ---- the noise tiles ----

    /// <summary>Makes the noise tile the first time it is needed.</summary>
    private WriteableBitmap Tile()
    {
        _tile ??= Bitmap(Grain.TileSize, TileBytes());
        return _tile;
    }

    private byte[] TileBytes()
    {
        _tileBytes ??= Grain.Tile();
        return _tileBytes;
    }

    /// <summary>A bitmap of the given height holding the given pixels (blue, green, red, alpha; colour already multiplied by alpha).</summary>
    private static WriteableBitmap Bitmap(int height, byte[] pixels)
    {
        var bitmap = new WriteableBitmap(Grain.TileSize, height);

        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Lays out enough copies of the tile to cover the window, adding or
    /// removing copies as the window grows or shrinks. Cheap to call often.
    /// </summary>
    private void Tiles()
    {
        var size = Grain.TileSize;
        var columns = (int)Math.Ceiling(root.ActualWidth / size);
        var rows = (int)Math.Ceiling(root.ActualHeight / size);
        var need = columns * rows;

        for (var i = 0; i < need; i++)
        {
            Image image;

            if (i < grain.Children.Count)
            {
                image = (Image)grain.Children[i];
            }
            else
            {
                image = new Image
                {
                    Source = Tile(),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };
                grain.Children.Add(image);
            }

            Canvas.SetLeft(image, i % columns * size);
            Canvas.SetTop(image, i / columns * size);
        }

        while (grain.Children.Count > need)
            grain.Children.RemoveAt(grain.Children.Count - 1);
    }

    // ---- the floating sidebar ----

    /// <summary>
    /// The sidebar, when it floats over the page (hidden, or peeking), is a solid
    /// panel, and it hides the window's grain behind it. So the panel carries a
    /// layer of its own, under its content: a brush made of a strip of the noise
    /// tile repeated downwards (a brush cannot repeat by itself), which a rounded
    /// border then clips to the panel's corners. The layer is pulled out over the
    /// panel's padding so that it covers the whole panel, and it is hidden while
    /// the sidebar is docked, because then the window's own grain shows through.
    /// Called whenever the texture, the window's size or the sidebar's state change.
    /// </summary>
    private void SideGrain()
    {
        var t = App.Look.Cur;
        var show = t.Texture > 0 && !_dock;

        sidegrain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        sidegrain.Opacity = Grain.Opacity(t.Texture);

        var pad = side.Padding;
        sidegrain.Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom);

        // The strip has to be as tall as the panel, which is at most as tall as the window.
        // It only ever grows: a strip that is too tall is cut off by the panel.
        var rows = Math.Max(1, (int)Math.Ceiling(root.ActualHeight / Grain.TileSize));
        if (rows > _sideRows)
        {
            var tile = TileBytes();
            var strip = new byte[tile.Length * rows];
            for (var i = 0; i < rows; i++)
                Buffer.BlockCopy(tile, 0, strip, i * tile.Length, tile.Length);

            sidegrain.Background = new ImageBrush
            {
                ImageSource = Bitmap(Grain.TileSize * rows, strip),
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            _sideRows = rows;
        }
    }
}
