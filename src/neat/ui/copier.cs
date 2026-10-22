using Windows.ApplicationModel.DataTransfer;

namespace neat;

/// <summary>
/// The clipboard, for the places that only need to put a piece of text on it.
/// Not called Clip: every UIElement already has a property of that name, so inside a
/// control "Clip.Text" would mean the element's own Clip and not this class.
/// </summary>
internal static class Copier
{
    /// <summary>
    /// Puts text on the clipboard. False when it did not work: the clipboard can be held by
    /// another program for an instant, and that must not end the app.
    /// </summary>
    public static bool Text(string text)
    {
        try
        {
            var pk = new DataPackage();
            pk.SetText(text);
            Clipboard.SetContent(pk);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[copier] copy failed: " + ex.Message);
            return false;
        }
    }
}
