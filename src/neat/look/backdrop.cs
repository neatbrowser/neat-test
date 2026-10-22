using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>Which blur Windows draws behind the window.</summary>
public enum BackdropKind
{
    /// <summary>Blurs whatever is behind the window (desktop, other windows). This is what Zen uses by default.</summary>
    Acrylic,

    /// <summary>Only tints with the desktop wallpaper, very soft. Windows 11 only.</summary>
    Mica,

    /// <summary>Like Mica but a little stronger. Windows 11 only.</summary>
    MicaAlt,
}

/// <summary>
/// The blur behind the window, as in Zen Browser. It is not drawn by us: Windows
/// draws it behind the whole window, and the window colour (see <see cref="Look"/>)
/// is laid over it with some transparency, so the blurred desktop shows through.
/// Zen does the same: it turns on the system backdrop (its pref
/// widget.windows.mica.toplevel-backdrop, whose default 2 is Acrylic) and then
/// makes every layer of its own UI transparent.
///
/// If the system cannot draw the chosen backdrop, nothing is set and
/// <see cref="Active"/> stays false, so <see cref="Look"/> falls back to solid
/// colours and the window looks as it did before.
/// </summary>
public static class Backdrop
{
    /// <summary>The backdrop to ask for. Change this one line to switch between Acrylic and Mica.</summary>
    public const BackdropKind Wanted = BackdropKind.Acrylic;

    /// <summary>True once a backdrop is on, so the window colour may be transparent.</summary>
    public static bool Active { get; private set; }

    /// <summary>Turns the backdrop on for a window. Call it before the window is painted.</summary>
    public static void Apply(Window window)
    {
        try
        {
            SystemBackdrop? backdrop = Wanted switch
            {
                BackdropKind.Mica => MicaController.IsSupported()
                    ? new MicaBackdrop()
                    : null,
                BackdropKind.MicaAlt => MicaController.IsSupported()
                    ? new MicaBackdrop { Kind = MicaKind.BaseAlt }
                    : null,
                _ => DesktopAcrylicController.IsSupported()
                    ? new DesktopAcrylicBackdrop()
                    : null,
            };

            if (backdrop is null)
                return;

            window.SystemBackdrop = backdrop;
            Active = true;
        }
        catch (Exception ex)
        {
            // A missing blur must never stop the browser from opening.
            Active = false;
            Log.Error("look", ex, "backdrop failed");
        }
    }
}
