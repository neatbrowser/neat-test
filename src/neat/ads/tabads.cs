namespace neat;

/// <summary>What the blocker remembers about one tab.</summary>
internal sealed class TabAds
{
    /// <summary>Address of the page the tab shows. It is the "source" of every request the page makes.</summary>
    public string Page = string.Empty;

    /// <summary>Address the last top-level navigation started with, until its document request has been seen.</summary>
    public string Nav = string.Empty;

    /// <summary>True from NavigationStarting until the page's own document request arrives.</summary>
    public bool NavPending;

    /// <summary>Counts top-level navigations; tells a script added for an old page from one for the current page.</summary>
    public int Gen;

    /// <summary>Requests stopped since the tab last started a new page.</summary>
    public int Blocked;

    /// <summary>The cosmetic rules of the top frame. Its exceptions are handed back when generic rules are asked for.</summary>
    public Cosmetic Top = Cosmetic.Empty;

    /// <summary>Scripts registered for this page, by host, so they can be removed when the next page starts.</summary>
    public readonly Dictionary<string, string> Scripts = new();
}
