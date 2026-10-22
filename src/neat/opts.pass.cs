using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The Passwords page: the saved logins, with show, copy and delete behind the gate (Windows
/// Hello, or the NEAT password when Hello cannot be used), and the choice of how the vault is
/// protected. The rules live in vault/ (vault.cs, gate.cs); this file only draws them and
/// passes on what the person does. Dialogs are not used: a task such as adding a login opens as
/// a card on the page itself (opts.pass.task.cs).
///
/// Rules this page keeps:
///   - Every handler goes through Safe, so a failure is logged and never leaves the callback
///     (an exception that does ends the app).
///   - Anything slow (opening the vault, making a password key) runs on a pool thread.
///   - After every wait the page checks it is still on show before it touches the controls.
///   - Leaving the page, or closing the window, ends the sign-in and shuts a password vault.
///   - Nothing secret goes to the log. A shown password is kept in a string only while it is shown.
/// </summary>
public sealed partial class Opts
{
    // Where the page stands in Pages (opts.xaml.cs).
    private const int PassAt = 2;

    private const string Dots = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";
    private const string Sep = "  \u00B7  ";

    // A shown password goes back to dots after this long.
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(20);

    // How often an unused sign-in is looked for.
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);

    // What the last Open of the shared vault said. The vault is read once per run of the app.
    private static OpenResult? _vaultOpen;

    private Gate? _gate;
    private SecretClip? _clip;
    private DispatcherQueueTimer? _sweep;
    private DispatcherQueueTimer? _hide;

    // The parts of the page while it is on show. All of them are null when another page is.
    private StackPanel? _body;
    private StackPanel? _task;
    private StackPanel? _rows;
    private TextBlock? _note;
    private TextBlock? _taskMsg;
    private TextBox? _find;

    private Protection _prot = Protection.None;
    private List<LoginInfo> _logins = new();
    private string _query = string.Empty;

    // The one password on show, if any: which entry and its text.
    private Guid? _shown;
    private string? _shownText;

    // ---- building ----

    private UIElement BuildPass()
    {
        _gate ??= new Gate(App.Vault, new Hello(() => WinRT.Interop.WindowNative.GetWindowHandle(this)));
        _clip ??= new SecretClip(DispatcherQueue);
        _sweep ??= MakeTimer(SweepEvery, true, Sweep);
        _hide ??= MakeTimer(ShowFor, false, HideShown);

        _note = new TextBlock
        {
            FontSize = 12,
            Foreground = _skin.Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 12, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _task = new StackPanel();
        _body = new StackPanel();
        _query = string.Empty;

        _sweep.Start();
        _ = Safe(RefreshPass);

        return Block.Page(_note, _task, _body);
    }

    /// <summary>Leaves the page: ends the sign-in, shuts a password vault and lets go of the controls.</summary>
    private void LeavePass()
    {
        _sweep?.Stop();
        _hide?.Stop();

        _shown = null;
        _shownText = null;
        _gate?.SignOut();

        _body = null;
        _task = null;
        _rows = null;
        _note = null;
        _taskMsg = null;
        _find = null;
    }

    private DispatcherQueueTimer MakeTimer(TimeSpan every, bool repeating, Action tick)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = every;
        t.IsRepeating = repeating;
        t.Tick += (s, e) =>
        {
            try
            {
                tick();
            }
            catch (Exception ex)
            {
                Log.Error("pass", ex, "timer failed");
            }
        };

        return t;
    }

    /// <summary>
    /// Runs something the person asked for. A failure is logged here and goes no further, and
    /// the person is told something went wrong instead of being left with a button that did nothing.
    /// </summary>
    private async Task Safe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error("pass", ex, "page action failed");
            Say("That did not work. The details are in crash.log in the data folder (see About).");
        }
    }

    private void Sweep()
    {
        if (_gate?.Tick() == true)
        {
            _shown = null;
            _shownText = null;
            _ = Safe(RefreshPass);
        }
    }

    private void HideShown()
    {
        _hide?.Stop();
        _shown = null;
        _shownText = null;
        RenderList();
    }

    // ---- reading the vault and drawing the page ----

    /// <summary>Asks the vault and Windows Hello how things stand, then draws the page again.</summary>
    private async Task RefreshPass()
    {
        var body = _body;
        var gate = _gate;
        if (body is null || gate is null)
            return;

        var vault = App.Vault;

        // The shared vault is read the first time it is needed. A file that could not be used
        // leaves it unread, so it is tried again (and reported again) on the next visit.
        if (vault.Mode is null)
        {
            try
            {
                _vaultOpen = await Task.Run(() => vault.Open());
            }
            catch (Exception ex)
            {
                Log.Error("pass", ex, "opening the vault failed");
                _vaultOpen = OpenResult.Unreadable;
            }

            if (_body != body)
                return;
        }

        var protection = await gate.ProtectionAsync();
        if (_body != body)
            return;

        _prot = protection;
        _logins = vault.IsOpen ? vault.List().ToList() : new List<LoginInfo>();

        Draw();
    }

    private void Draw()
    {
        var body = _body;
        if (body is null)
            return;

        var vault = App.Vault;
        body.Children.Clear();
        _rows = null;
        _find = null;

        // A vault that cannot be opened: say why, and touch nothing.
        if (vault.Mode is null || (vault.Mode == VaultMode.Dpapi && !vault.IsOpen))
        {
            body.Children.Add(Block.Head(_skin, "Passwords"));
            body.Children.Add(Block.Card(_skin, Explain(Problem(vault))));
            body.Children.Add(Block.Head(_skin, "What the vault protects"));
            body.Children.Add(Block.Card(_skin, Explain(Honest)));
            return;
        }

        // A password vault that is shut: only the password opens it.
        if (!vault.IsOpen)
        {
            body.Children.Add(Block.Head(_skin, "Unlock"));
            body.Children.Add(UnlockCard());
            body.Children.Add(Block.Head(_skin, "What the vault protects"));
            body.Children.Add(Block.Card(_skin, Explain(Honest)));
            return;
        }

        body.Children.Add(Block.Head(_skin, "Protection"));
        body.Children.Add(ProtectionCard());
        body.Children.Add(Block.Head(_skin, "Saved logins"));
        body.Children.Add(Toolbar());

        _rows = new StackPanel();
        body.Children.Add(Block.Card(_skin, _rows));
        RenderList();

        body.Children.Add(Block.Head(_skin, "What the vault protects"));
        body.Children.Add(Block.Card(_skin, Explain(Honest)));
    }

    private string Problem(Vault vault)
    {
        // A vault under Windows protection that is still shut means Windows would not open its key.
        if (vault.Mode == VaultMode.Dpapi || _vaultOpen == OpenResult.KeyLost)
        {
            return "Windows would not open the key to the saved logins. This happens when the vault "
                + "was made by another Windows user, or this Windows account was reset. NEAT has not "
                + "changed or deleted vault.json (it is in the data folder, see About), so it can "
                + "still be opened where it was made.";
        }

        if (_vaultOpen == OpenResult.TooNew)
        {
            return "vault.json was made by a newer NEAT. Update NEAT to open it. The file has not been changed.";
        }

        return "NEAT could not read vault.json. The file has not been changed. If you did not "
            + "edit it, the details are in crash.log in the data folder.";
    }

    private const string Honest =
        "It protects against someone who gets a copy of the vault file, the disk or a backup, "
        + "against another Windows account, and against web pages. It does not protect against a "
        + "program that is already running as you on an unlocked PC: nothing in a browser can. "
        + "The addresses of the sites are stored in the clear, so someone with the file can see "
        + "which sites you have logins for; user names and passwords are not.";

    private UIElement ProtectionCard()
    {
        string status;
        string text;
        var tools = new List<Button>();

        switch (_prot)
        {
            case Protection.Hello:
                status = "Windows Hello";
                text = "Showing, copying or deleting a password asks for your fingerprint, face or PIN first.";
                tools.Add(Btn("Use a NEAT password", () => Start(TaskSetPassword)));
                break;

            case Protection.Password:
                status = "NEAT password";
                text = "The vault opens only with your password, and shuts again after "
                    + (int)Gate.Idle.TotalMinutes + " minutes without use.";
                tools.Add(Btn("Change password", () => Start(TaskChangePassword)));
                tools.Add(Btn("Lock now", LockNow));

                if (_gate!.Hello == HelloState.Ready)
                    tools.Add(Btn("Use Windows Hello", () => Start(TaskUseHello)));

                break;

            default:
                status = "Not protected yet";
                text = HelloText() + " Until you set a NEAT password, saved passwords cannot be shown, "
                    + "copied or deleted, and anyone who can sign in to this Windows account can read the vault.";
                tools.Add(Btn("Set a NEAT password", () => Start(TaskSetPassword)));
                break;
        }

        return Block.Card(_skin,
            Block.Row(_skin, "Protected by", null, Block.Value(_skin, status)),
            Explain(text),
            Actions(tools.ToArray()));
    }

    private string HelloText()
    {
        return _gate!.Hello switch
        {
            HelloState.NotSetUp => "Windows Hello is not set up for your Windows account.",
            HelloState.NoDevice => "This PC has nothing Windows Hello can use (no fingerprint reader, camera or PIN).",
            HelloState.Blocked => "Windows Hello is turned off by a policy on this PC.",
            _ => "Windows Hello could not be used just now.",
        };
    }

    private UIElement UnlockCard()
    {
        var box = new PasswordBox { Width = 200 };

        async Task Go()
        {
            var typed = box.Password;
            if (typed.Length == 0)
                return;

            var body = _body;
            Say("Checking...");

            var result = await Task.Run(() => _gate!.SignIn(typed.AsSpan()));
            if (_body != body)
                return;

            if (result == VaultResult.Ok)
            {
                Say(string.Empty);
                await RefreshPass();
                return;
            }

            box.Password = string.Empty;
            Say(Why(result));
        }

        box.KeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                await Safe(Go);
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(box);
        tools.Children.Add(Btn("Unlock", Go));

        return Block.Card(_skin,
            Block.Row(_skin, "NEAT password", "Your saved logins are locked. A forgotten password cannot be recovered.", tools));
    }

    // ---- the list of logins ----

    private UIElement Toolbar()
    {
        _find = new TextBox { PlaceholderText = "Search logins", Text = _query };
        _find.TextChanged += (s, e) =>
        {
            _query = _find?.Text ?? string.Empty;
            RenderList();
        };

        var add = Btn("Add", () => Start(TaskAdd));

        var bar = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(add, 1);
        bar.Children.Add(_find);
        bar.Children.Add(add);

        return bar;
    }

    private void RenderList()
    {
        var rows = _rows;
        if (rows is null)
            return;

        rows.Children.Clear();

        var q = _query.Trim();
        var shown = _logins
            .Where(l => q.Length == 0
                || l.Site.Contains(q, StringComparison.OrdinalIgnoreCase)
                || l.User.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        if (shown.Count == 0)
        {
            rows.Children.Add(Explain(_logins.Count == 0
                ? "No saved logins yet. Add one with the Add button."
                : "Nothing matches your search."));
            return;
        }

        for (var i = 0; i < shown.Count; i++)
        {
            // The line goes between rows, never above the first or below the last.
            if (i > 0)
                rows.Children.Add(new Border { Height = 1, Background = _skin.Line });

            rows.Children.Add(LoginRow(shown[i]));
        }
    }

    private UIElement LoginRow(LoginInfo it)
    {
        var seen = _shown == it.Id;
        var who = it.User.Length == 0 ? "(no user name)" : it.User;

        var second = it.Damaged
            ? "This entry does not check out: it was changed outside NEAT."
            : who + Sep + (seen ? _shownText : Dots);

        // Without a way to prove who is asking, nothing secret is offered.
        var usable = _prot != Protection.None && !it.Damaged;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(IconBtn("\uE890", seen ? "Hide password" : "Show password", () => ToggleShow(it), usable));
        tools.Children.Add(IconBtn("\uE8C8", "Copy password", () => CopyRow(it), usable));
        tools.Children.Add(IconBtn("\uE74D", "Delete", () => DeleteRow(it), _prot != Protection.None));

        return Block.Row(_skin, it.Site, second, tools);
    }

    // ---- what the buttons on a row do ----

    /// <summary>
    /// The sign-in the person has just passed, or null with a note saying why not. Windows Hello
    /// is asked when there is no sign-in yet. A password vault is shut when its sign-in runs
    /// out, so there the page is just drawn again, and shows the password box.
    /// </summary>
    private async Task<Access?> Authorize(string why)
    {
        var gate = _gate;
        if (gate is null)
            return null;

        if (gate.Current is { } have)
        {
            gate.Touch();
            return have;
        }

        if (_prot == Protection.Hello)
        {
            var body = _body;
            var yes = await gate.SignInAsync(why);
            if (_body != body)
                return null;

            if (yes)
                return gate.Current;

            Say("Windows Hello did not confirm, so nothing was shown. If it never opens, "
                + "protect the vault with a NEAT password instead.");
            return null;
        }

        if (_prot == Protection.Password)
        {
            await RefreshPass();
            return null;
        }

        Say("Set a NEAT password first: saved passwords cannot be shown or copied without one.");
        return null;
    }

    private async Task ToggleShow(LoginInfo it)
    {
        if (_shown == it.Id)
        {
            HideShown();
            return;
        }

        var access = await Authorize("Show the saved password for " + it.Site);
        if (access is null || _body is null)
            return;

        using var secret = App.Vault.Reveal(it.Id, access);
        if (secret is null)
        {
            Say("This entry could not be opened. It may have been changed outside NEAT.");
            return;
        }

        _shown = it.Id;
        _shownText = secret.Text();
        _hide?.Stop();
        _hide?.Start();

        Say(string.Empty);
        RenderList();
    }

    private async Task CopyRow(LoginInfo it)
    {
        var access = await Authorize("Copy the saved password for " + it.Site);
        if (access is null || _body is null)
            return;

        using var secret = App.Vault.Reveal(it.Id, access);
        if (secret is null)
        {
            Say("This entry could not be opened. It may have been changed outside NEAT.");
            return;
        }

        _clip?.Copy(secret.Text());
        Say("Copied. It is taken off the clipboard after " + (int)SecretClip.Keep.TotalSeconds + " seconds.");
    }

    private async Task DeleteRow(LoginInfo it)
    {
        var access = await Authorize("Delete the saved login for " + it.Site);
        if (access is null || _body is null)
            return;

        TaskDelete(it);
    }

    private async Task LockNow()
    {
        _shown = null;
        _shownText = null;
        _gate?.SignOut();
        await RefreshPass();
    }
}
