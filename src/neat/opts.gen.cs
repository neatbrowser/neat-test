using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>The General page: what opens at startup, the home page and the search engine.</summary>
public sealed partial class Opts
{
    // The controls of this page while it is on show; null when another page is.
    private TextBox? _home;
    private ComboBox? _box;

    private UIElement BuildGen()
    {
        _home = new TextBox { Width = 280, Text = App.Cfg.Cur.Home };

        // Saved on Enter or when the box loses focus, never on every key.
        _home.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                Flush();
        };
        _home.LostFocus += (s, e) => Flush();

        var box = new ComboBox { Width = 280 };
        foreach (var x in App.Cfg.Cur.Engines)
            box.Items.Add(x.Name);

        // Show the engine that is really in use: when the saved name matches
        // nothing, Search falls back to the first engine, and so does the list.
        // The index is set before the handler is added, so filling the list saves nothing.
        var use = App.Find.Use.Name;
        box.SelectedIndex = App.Cfg.Cur.Engines.FindIndex(x => x.Name == use);
        box.SelectionChanged += (s, e) =>
        {
            var prefs = App.Cfg.Cur;
            var i = box.SelectedIndex;
            if (i < 0 || i >= prefs.Engines.Count || prefs.Engines[i].Name == prefs.Use)
                return;

            prefs.Use = prefs.Engines[i].Name;
            App.Cfg.Save();
        };
        _box = box;

        // Which of the two is on is set before the handlers are added, so building the page saves nothing.
        var start = App.Cfg.Cur.Start;
        var home = Block.Choice(_skin, "Open the home page", "start", start == StartMode.Home);
        var last = Block.Choice(_skin, "Continue where you left off", "start", start == StartMode.Last);
        home.Checked += (s, e) => SetStart(StartMode.Home);
        last.Checked += (s, e) => SetStart(StartMode.Last);

        return Block.Page(
            Block.Head(_skin, "On startup"),
            Block.Card(_skin, home, last),
            Block.Head(_skin, "Home page"),
            Block.Card(_skin,
                Block.Row(_skin, "Home page", "Opens at startup when \"Open the home page\" is chosen above, and with Alt+Home.", _home)),
            Block.Head(_skin, "Search"),
            Block.Card(_skin,
                Block.Row(_skin, "Search engine", "Used for anything typed in the address bar that is not a web address.", box)));
    }

    private static void SetStart(StartMode mode)
    {
        if (App.Cfg.Cur.Start == mode)
            return;

        App.Cfg.Cur.Start = mode;
        App.Cfg.Save();
    }

    /// <summary>
    /// Saves the home page if it was edited. The text is stored as typed; it is
    /// turned into an address (App.Find.Resolve) when it is used.
    /// </summary>
    private void Flush()
    {
        if (_home is null)
            return;

        var text = _home.Text;

        // An empty home page is not allowed: the store would put the default back at the
        // next start. So the box goes back to what is saved instead of saving nothing.
        if (string.IsNullOrWhiteSpace(text))
        {
            _home.Text = App.Cfg.Cur.Home;
            return;
        }

        if (text == App.Cfg.Cur.Home)
            return;

        App.Cfg.Cur.Home = text;
        App.Cfg.Save();
    }
}
