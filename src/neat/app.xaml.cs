using Microsoft.UI.Xaml;

namespace neat;

public partial class App : Application
{
    private Window? _win;

    // Shared by every window. Set up once in OnLaunched, before the first window opens.
    public static Store Cfg { get; private set; } = null!;
    public static Session Sess { get; private set; } = null!;
    public static History Hist { get; private set; } = null!;
    public static Bookmarks Marks { get; private set; } = null!;
    public static Downloads Dl { get; private set; } = null!;
    public static Search Find { get; private set; } = null!;
    public static Look Look { get; private set; } = null!;
    internal static Adblock Ads { get; private set; } = null!;

    private static Vault? _vault;

    /// <summary>
    /// The password vault, shared by every window. It is made the first time it is asked for, and
    /// even then nothing is read or created until something calls Open on it, so a person who
    /// never uses passwords never gets a vault file.
    /// </summary>
    public static Vault Vault => _vault ??= new Vault(Path.Combine(Env.Root, "vault.json"));

    public App()
    {
        // Must run before any WebView2 control is created, so the bundled
        // runtime and the profile folder are picked up.
        Env.Configure();

        // Anything that goes wrong without being caught is written to
        // crash.log before the process dies. None of these handlers marks the
        // error as handled, so the app behaves exactly as it did; they only
        // leave a trace.
        Log.Start();
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Error("domain", e.ExceptionObject as Exception, $"unhandled exception, terminating={e.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (s, e) =>
            Log.Error("task", e.Exception, "unobserved task exception");

        InitializeComponent();

        UnhandledException += (s, e) => Log.Error("xaml", e.Exception, e.Message);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Cfg = new Store(Path.Combine(Env.Root, "settings.json"));
        Cfg.Load();
        Sess = new Session(Path.Combine(Env.Root, "session.json"));
        Find = new Search(Cfg);
        Look = new Look(Cfg);

        // The blocker builds its engine in the background; the first tab waits for it (see Win.Open).
        Ads = new Adblock();
        _ = Ads.Start();

        var db = new Db(Path.Combine(Env.Root, "browser.db"));
        Hist = new History(db);
        Marks = new Bookmarks(db);
        Dl = new Downloads(db);

        try
        {
            await Hist.Init();
            await Marks.Init();
        }
        catch (Exception ex)
        {
            // A broken database must not stop the browser from opening. The
            // window reports the problem where the history numbers would be.
            System.Diagnostics.Debug.WriteLine("[app] database init failed: " + ex.Message);
            Log.Error("db", ex, "database init failed");
        }

        // Downloads have a try of their own: a problem with the history tables must not
        // take the download list with it. If this fails the list is empty and new downloads
        // still work; they are just not remembered.
        try
        {
            await Dl.Init();
        }
        catch (Exception ex)
        {
            Log.Error("db", ex, "downloads init failed");
        }

        _win = new Win();
        _win.Activate();
    }
}
