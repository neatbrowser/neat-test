using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace neat;

/// <summary>
/// Copies a password to the clipboard in a way that leaves as little trace as it can, and takes
/// it off the clipboard again after <see cref="Keep"/>.
///
///   - It is marked as not for the Windows clipboard history (Win+V) and not for the cloud
///     clipboard that syncs between PCs.
///   - After <see cref="Keep"/> it is cleared, but only if the clipboard still holds it: if
///     the person has copied something else since, that is left alone.
///   - <see cref="ClearNow"/> (the settings window closing) clears it without that check,
///     because after the window is gone nothing would clear it later. This also removes
///     something else copied in those few seconds; leaving a password behind is the worse of
///     the two.
///
/// A password copied to the clipboard can still be read by any program running as the user
/// while it is there. This only shortens that time.
///
/// This file is pass/clip.cs and the class is SecretClip. ui/clip.cs holds a different class,
/// Clip, the plain "put this text on the clipboard" helper for everything else; it keeps no
/// history exclusion and clears nothing, which is why a password does not use it.
/// </summary>
internal sealed class SecretClip
{
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueueTimer _timer;

    // What was put on the clipboard, kept only until it is cleared, to know it is still ours.
    private string? _text;

    public SecretClip(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = Keep;
        _timer.IsRepeating = false;
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            _ = ClearIfOurs();
        };
    }

    public void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        var options = new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        };

        Clipboard.SetContentWithOptions(package, options);

        _text = text;
        _timer.Stop();
        _timer.Start();
    }

    public void ClearNow()
    {
        _timer.Stop();

        if (_text is null)
            return;

        _text = null;

        try
        {
            Clipboard.Clear();
        }
        catch (Exception ex)
        {
            Log.Error("clip", ex, "could not clear the clipboard");
        }
    }

    private async Task ClearIfOurs()
    {
        var mine = _text;
        _text = null;

        if (mine is null)
            return;

        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
                return;

            var now = await view.GetTextAsync();
            if (now == mine)
                Clipboard.Clear();
        }
        catch (Exception ex)
        {
            Log.Error("clip", ex, "could not clear the clipboard");
        }
    }
}
