using System.Text;
using Windows.System;

namespace neat;

/// <summary>The groups the Shortcuts page lists shortcuts under, in the order it shows them.</summary>
internal enum Area { Tabs, Navigation, Page, Window }

/// <summary>
/// One keyboard shortcut: the keys, what they do, and how the Shortcuts page
/// lists them. Shortcuts with the same Name (Ctrl+R and F5 both reload) share one row there.
/// </summary>
internal sealed record Cmd(VirtualKey Key, VirtualKeyModifiers Mod, Action Run, Area Area, string Name);

/// <summary>
/// Keyboard shortcuts. Every shortcut is registered once through Accel() (in
/// win.side.cs), which puts it in the table below. From that single table:
///
///   - focus in the window's own controls: each entry becomes a XAML
///     KeyboardAccelerator (done by Accel itself);
///   - focus inside a web page: WebView2 keeps those keys for itself and WinUI
///     never sees them, so a small script, added to every page, spots the same
///     combinations and tells the app which one was pressed.
///
/// F5, Ctrl+F (find), Ctrl+P (print), F12 and Ctrl+Shift+I (DevTools) and the
/// zoom keys are left to WebView2, which already handles them in the page.
/// </summary>
public sealed partial class Win
{
    private readonly List<Cmd> _cmds = new();

    /// <summary>Every registered shortcut, in the order they were registered. The Shortcuts page in settings lists these.</summary>
    internal IReadOnlyList<Cmd> Cmds => _cmds;

    // The comma key (VK_OEM_COMMA). VirtualKey has no name for it, and without a name
    // JsKey could not tell a page script what to look for.
    private const VirtualKey Comma = (VirtualKey)0xBC;

    // Messages from pages must carry this, so a page cannot trigger commands by itself.
    private readonly string _tok = Guid.NewGuid().ToString("N");
    private string? _js;

    private void KeysInit()
    {
        const VirtualKeyModifiers C = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers S = VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers A = VirtualKeyModifiers.Menu;   // Alt

        // Tabs
        Accel(VirtualKey.W, C, Area.Tabs, "Close tab", () => { if (_cur is not null) Shut(_cur); });
        Accel(VirtualKey.Tab, C, Area.Tabs, "Next tab", () => Step(1));
        Accel(VirtualKey.Tab, C | S, Area.Tabs, "Previous tab", () => Step(-1));
        Accel(VirtualKey.T, C | S, Area.Tabs, "Reopen closed tab", Reopen);

        // Ctrl+1..8 pick that tab, Ctrl+9 the last one.
        for (var n = 1; n <= 8; n++)
        {
            var i = n - 1;
            Accel(VirtualKey.Number0 + n, C, Area.Tabs, "Go to tab", () => Nth(i));
        }
        Accel(VirtualKey.Number9, C, Area.Tabs, "Go to last tab", () => Nth(_tabs.Count - 1));

        // Navigation
        Accel(VirtualKey.Left, A, Area.Navigation, "Back", Back);
        Accel(VirtualKey.Right, A, Area.Navigation, "Forward", Fwd);
        Accel(VirtualKey.Home, A, Area.Navigation, "Go to home page", GoHome);
        Accel(VirtualKey.R, C, Area.Navigation, "Reload", Reload);
        Accel(VirtualKey.F5, VirtualKeyModifiers.None, Area.Navigation, "Reload", Reload);
        Accel(VirtualKey.R, C | S, Area.Navigation, "Reload without the cache", () => _ = HardReload());
        Accel(VirtualKey.F5, C, Area.Navigation, "Reload without the cache", () => _ = HardReload());

        // Page and window
        Accel(VirtualKey.D, C, Area.Page, "Bookmark this page", () => _ = ToggleMark());
        Accel(VirtualKey.W, C | S, Area.Window, "Close window", Close);
        Accel(Comma, C, Area.Window, "Settings", ShowOpts);
        Accel(VirtualKey.F11, VirtualKeyModifiers.None, Area.Window, "Full screen", ToggleFull);

        // The Library (win.lib.cs). Esc is not here: everything in this table is also swallowed
        // inside web pages, which would take Esc away from every site. LibView handles it.
        Accel(VirtualKey.H, C, Area.Window, "Open History", () => ShowLib(Sec.History));
        Accel(VirtualKey.J, C, Area.Window, "Open Downloads", () => ShowLib(Sec.Downloads));
        Accel(VirtualKey.O, C | S, Area.Window, "Open Bookmarks", () => ShowLib(Sec.Bookmarks));
    }

    // ---- what the shortcuts do ----

    /// <summary>Selects the tab next to the current one, wrapping around.</summary>
    private void Step(int d)
    {
        if (_cur is null || _tabs.Count < 2)
            return;

        var n = _tabs.Count;
        Pick(_tabs[(_tabs.IndexOf(_cur) + d + n) % n]);
    }

    private void Nth(int i)
    {
        if (i >= 0 && i < _tabs.Count)
            Pick(_tabs[i]);
    }

    private void GoHome()
    {
        var to = App.Find.Resolve(App.Cfg.Cur.Home);
        var core = _cur?.View.CoreWebView2;
        if (to is not null && core is not null)
            core.Navigate(to);
    }

    /// <summary>Reload that ignores the cache.</summary>
    private async Task HardReload()
    {
        var core = _cur?.View.CoreWebView2;
        if (core is null)
            return;

        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[keys] hard reload failed: " + ex.Message);
            core.Reload();
        }
    }

    // ---- shortcuts while focus is inside a web page ----

    /// <summary>The key combination as the page script names it, e.g. "ctrl+shift+t".</summary>
    private static string Combo(Cmd c)
    {
        var s = new StringBuilder();
        if (c.Mod.HasFlag(VirtualKeyModifiers.Control)) s.Append("ctrl+");
        if (c.Mod.HasFlag(VirtualKeyModifiers.Menu)) s.Append("alt+");
        if (c.Mod.HasFlag(VirtualKeyModifiers.Shift)) s.Append("shift+");
        s.Append(JsKey(c.Key));
        return s.ToString();
    }

    /// <summary>
    /// The combinations of shortcuts that do the same thing, as the Shortcuts page writes them:
    /// "Ctrl+R, F5". A run of number keys on the same modifiers is written as a range,
    /// "Ctrl+1\u20138", so the eight tab shortcuts take one line, not eight.
    /// </summary>
    internal static string Show(IReadOnlyList<Cmd> same)
    {
        var first = same[0];
        var digits = same.All(c => c.Key >= VirtualKey.Number0 && c.Key <= VirtualKey.Number9 && c.Mod == first.Mod);

        if (digits && same.Count > 1)
        {
            var lo = same.Min(c => c.Key);
            var hi = same.Max(c => c.Key);
            return Mods(first.Mod) + KeyName(lo) + "\u2013" + KeyName(hi);
        }

        return string.Join(", ", same.Select(c => Mods(c.Mod) + KeyName(c.Key)));
    }

    private static string Mods(VirtualKeyModifiers m)
    {
        var s = new StringBuilder();
        if (m.HasFlag(VirtualKeyModifiers.Control)) s.Append("Ctrl+");
        if (m.HasFlag(VirtualKeyModifiers.Menu)) s.Append("Alt+");
        if (m.HasFlag(VirtualKeyModifiers.Shift)) s.Append("Shift+");
        return s.ToString();
    }

    /// <summary>A key's name as it is written on the keyboard, e.g. "W", "Tab", "F5", ",".</summary>
    private static string KeyName(VirtualKey k)
    {
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9)
            return ((int)k - (int)VirtualKey.Number0).ToString();

        return k == Comma ? "," : k.ToString();
    }

    /// <summary>A key's name as KeyboardEvent.key reports it, lower-cased.</summary>
    private static string JsKey(VirtualKey k)
    {
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9)
            return ((int)k - (int)VirtualKey.Number0).ToString();

        return k switch
        {
            VirtualKey.Left => "arrowleft",
            VirtualKey.Right => "arrowright",
            Comma => ",",
            _ => k.ToString().ToLowerInvariant(),   // A..Z, Tab, Home, F5
        };
    }

    /// <summary>
    /// The script added to every page. It listens for real key presses (never
    /// ones a page script made up), and when one matches a shortcut it stops
    /// the page from seeing it and posts a message to the app.
    /// </summary>
    private string Js()
    {
        if (_js is not null)
            return _js;

        var combos = string.Join(",", _cmds.Select(c => "\"" + Combo(c) + "\"").Distinct());

        _js = $$"""
            (() => {
              const w = window.chrome && window.chrome.webview;
              if (!w) return;
              const post = w.postMessage.bind(w);
              const keys = new Set([{{combos}}]);
              window.addEventListener('keydown', e => {
                if (!e.isTrusted || !e.key) return;
                const k = e.key.toLowerCase();
                if (k === 'control' || k === 'shift' || k === 'alt' || k === 'meta') return;
                const c = (e.ctrlKey ? 'ctrl+' : '') + (e.altKey ? 'alt+' : '') + (e.shiftKey ? 'shift+' : '') + k;
                if (!keys.has(c)) return;
                e.preventDefault();
                e.stopPropagation();
                post('neat:{{_tok}}:' + c);
              }, true);
            })();
            """;

        return _js;
    }

    /// <summary>Runs the shortcut a page reported, if the message is genuine.</summary>
    private void OnKey(Tab tab, string? msg)
    {
        var head = "neat:" + _tok + ":";
        if (msg is null || !msg.StartsWith(head, StringComparison.Ordinal) || tab != _cur)
            return;

        var combo = msg[head.Length..];
        var cmd = _cmds.FirstOrDefault(c => Combo(c) == combo);
        if (cmd is not null)
            DispatcherQueue.TryEnqueue(() => cmd.Run());
    }
}
