using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The Passwords page, second half: the small tasks that open as a card on the page (add a
/// login, set or change the NEAT password, go back to Windows Hello, confirm a delete), and the
/// little pieces of layout both halves use. One task is open at a time; opening another replaces it.
/// </summary>
public sealed partial class Opts
{
    // ---- small pieces ----

    /// <summary>A line of explanation inside a card.</summary>
    private TextBlock Explain(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = _skin.Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 12),
        };
    }

    /// <summary>A row of buttons inside a card.</summary>
    private static StackPanel Actions(params Button[] buttons)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 10, 16, 12),
        };

        foreach (var b in buttons)
            bar.Children.Add(b);

        return bar;
    }

    private Button Btn(string text, Func<Task> click)
    {
        var b = new Button { Content = text, MinWidth = 88 };
        b.Click += async (s, e) => await Safe(click);
        return b;
    }

    /// <summary>An icon-only button (16 is one of the sizes Microsoft recommends for glyphs).</summary>
    private Button IconBtn(string glyph, string tip, Func<Task> click, bool enabled)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            Background = _none,
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
        };

        ToolTipService.SetToolTip(b, tip);
        b.Click += async (s, e) => await Safe(click);
        return b;
    }

    /// <summary>Runs a synchronous step that opens a task, as a button action.</summary>
    private static Task Start(Action open)
    {
        open();
        return Task.CompletedTask;
    }

    /// <summary>The line of feedback at the top of the page. An empty text hides it.</summary>
    private void Say(string text)
    {
        if (_note is null)
            return;

        _note.Text = text;
        _note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Why(VaultResult result)
    {
        return result switch
        {
            VaultResult.Wrong => "That is not the password.",
            VaultResult.Wait => "Too many wrong tries. Try again in "
                + Math.Max(1, (int)Math.Ceiling(App.Vault.RetryAfter.TotalSeconds)) + " seconds.",
            VaultResult.TooShort => "The new password needs at least " + Vault.MinPassword + " characters.",
            VaultResult.Damaged => "Some saved logins do not check out (they were changed outside NEAT), "
                + "so the vault cannot be protected another way without losing them. Nothing was changed.",
            VaultResult.KeyLost => "Windows would not open the key to the vault.",
            _ => "That cannot be done right now.",
        };
    }

    // ---- the card a task opens ----

    private void ShowTask(string title, IEnumerable<UIElement> rows, params Button[] buttons)
    {
        var task = _task;
        if (task is null)
            return;

        _taskMsg = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var b in buttons)
            bar.Children.Add(b);

        var foot = new StackPanel { Spacing = 8, Margin = new Thickness(16, 10, 16, 12) };
        foot.Children.Add(_taskMsg);
        foot.Children.Add(bar);

        var all = rows.ToList();
        all.Add(foot);

        task.Children.Clear();
        task.Children.Add(Block.Head(_skin, title));
        task.Children.Add(Block.Card(_skin, all.ToArray()));
    }

    private void CloseTask()
    {
        _task?.Children.Clear();
        _taskMsg = null;
    }

    /// <summary>Says what went wrong inside the open task.</summary>
    private void Fail(string text)
    {
        if (_taskMsg is null)
            return;

        _taskMsg.Text = text;
        _taskMsg.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- the tasks ----

    private void TaskAdd()
    {
        var site = new TextBox { Width = 260, PlaceholderText = "example.com" };
        var user = new TextBox { Width = 260 };
        var pass = new PasswordBox { Width = 260 };

        async Task Save()
        {
            // "example.com" is taken as https://example.com.
            var typed = site.Text.Trim();
            var origin = Origin.Of(typed.Contains("://") ? typed : "https://" + typed);

            if (origin is null)
            {
                Fail("Enter the address of a web site, like example.com.");
                return;
            }

            if (pass.Password.Length == 0)
            {
                Fail("Enter the password.");
                return;
            }

            App.Vault.Add(origin, user.Text.Trim(), pass.Password);

            CloseTask();
            Say("Saved.");
            await RefreshPass();
        }

        ShowTask(
            "Add a login",
            new UIElement[]
            {
                Block.Row(_skin, "Web site", null, site),
                Block.Row(_skin, "User name", null, user),
                Block.Row(_skin, "Password", null, pass),
            },
            Btn("Save", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskSetPassword()
    {
        var first = new PasswordBox { Width = 260 };
        var again = new PasswordBox { Width = 260 };

        async Task Save()
        {
            var typed = first.Password;

            if (typed.Length < Vault.MinPassword)
            {
                Fail(Why(VaultResult.TooShort));
                return;
            }

            if (typed != again.Password)
            {
                Fail("The two passwords are not the same.");
                return;
            }

            // Changing how the vault is protected is as serious as reading it: with Windows
            // Hello available, it has to say yes first.
            if (_prot == Protection.Hello && await Authorize("Protect the saved logins with a NEAT password") is null)
                return;

            var body = _body;
            Fail(string.Empty);

            var result = await Task.Run(() => App.Vault.UsePassword(typed.AsSpan()));
            if (_body != body)
                return;

            if (result != VaultResult.Ok)
            {
                Fail(Why(result));
                return;
            }

            // The person has just typed the password twice: that counts as signing in.
            _gate?.Grant();
            CloseTask();
            Say("Your saved logins are now protected by your NEAT password.");
            await RefreshPass();
        }

        ShowTask(
            "Set a NEAT password",
            new UIElement[]
            {
                Block.Row(_skin, "New password", "At least " + Vault.MinPassword + " characters.", first),
                Block.Row(_skin, "Repeat it", null, again),
                Explain("NEAT cannot recover a forgotten password. If you forget it, the saved logins are lost. "
                    + "The vault will no longer open by itself for your Windows account."),
            },
            Btn("Protect with this password", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskChangePassword()
    {
        var current = new PasswordBox { Width = 260 };
        var first = new PasswordBox { Width = 260 };
        var again = new PasswordBox { Width = 260 };

        async Task Save()
        {
            var old = current.Password;
            var typed = first.Password;

            if (typed.Length < Vault.MinPassword)
            {
                Fail(Why(VaultResult.TooShort));
                return;
            }

            if (typed != again.Password)
            {
                Fail("The two new passwords are not the same.");
                return;
            }

            var body = _body;
            Fail(string.Empty);

            var result = await Task.Run(() => App.Vault.ChangePassword(old.AsSpan(), typed.AsSpan()));
            if (_body != body)
                return;

            if (result != VaultResult.Ok)
            {
                Fail(Why(result));
                return;
            }

            _gate?.Grant();
            CloseTask();
            Say("Your NEAT password was changed.");
            await RefreshPass();
        }

        ShowTask(
            "Change the NEAT password",
            new UIElement[]
            {
                Block.Row(_skin, "Current password", null, current),
                Block.Row(_skin, "New password", "At least " + Vault.MinPassword + " characters.", first),
                Block.Row(_skin, "Repeat it", null, again),
                Explain("NEAT cannot recover a forgotten password. If you forget it, the saved logins are lost."),
            },
            Btn("Change password", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskUseHello()
    {
        Task Save()
        {
            var result = App.Vault.UseDpapi();
            if (result != VaultResult.Ok)
            {
                Fail(Why(result));
                return Task.CompletedTask;
            }

            // From now on Windows Hello is what is asked, so the password sign-in ends here.
            _gate?.SignOut();
            CloseTask();
            Say("Your saved logins are now protected by Windows. The NEAT password is no longer needed.");
            return RefreshPass();
        }

        ShowTask(
            "Use Windows Hello instead",
            new UIElement[]
            {
                Explain("Windows will protect the key to your saved logins again, and Windows Hello will be "
                    + "asked before a password is shown or copied. The vault then opens by itself for your "
                    + "Windows account, so the NEAT password stops being needed."),
            },
            Btn("Use Windows Hello", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskDelete(LoginInfo it)
    {
        async Task Save()
        {
            var access = await Authorize("Delete the saved login for " + it.Site);
            if (access is null || _body is null)
                return;

            App.Vault.Remove(it.Id);

            if (_shown == it.Id)
                HideShown();

            CloseTask();
            Say("Deleted.");
            await RefreshPass();
        }

        var who = it.User.Length == 0 ? "the login" : "the login for " + it.User;

        ShowTask(
            "Delete a login",
            new UIElement[]
            {
                Explain("Delete " + who + " on " + it.Site + "? This cannot be undone."),
            },
            Btn("Delete", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }
}
