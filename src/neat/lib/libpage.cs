using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>The Library's sections, in the order of its rail.</summary>
public enum Sec
{
    Media,
    Downloads,
    Bookmarks,
    History,
}

/// <summary>
/// One section of the Library. LibView shows the rail, the search row and the back
/// arrow, and puts the page's View under the search row. A new section is a new
/// class like this one plus one line in LibView.Pages(); nothing else has to change.
///
/// A page keeps its own state (the query, the sort) and its own list, and is
/// told by LibView when to load, search and stop.
/// </summary>
internal abstract class LibPage
{
    /// <summary>The label under the icon in the rail.</summary>
    public abstract string Name { get; }

    /// <summary>The icon in the rail. Must be in both Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
    public abstract string Glyph { get; }

    /// <summary>What goes under the search row.</summary>
    public abstract UIElement View { get; }

    /// <summary>Whether the search box and the Filter button are shown for this page.</summary>
    public virtual bool Searchable => false;

    /// <summary>Placeholder text of the search box.</summary>
    public virtual string Hint => "Search";

    /// <summary>
    /// Called every time the section is shown, so the list is never stale. Query and
    /// filter are back to their defaults. Runs on the UI thread; slow work (the database)
    /// must be moved off it with Task.Run.
    /// </summary>
    public abstract Task Load();

    /// <summary>The search box changed (already held back for a moment while typing).</summary>
    public virtual void Search(string text)
    {
    }

    /// <summary>The Filter button's menu, built fresh each time the section is shown; null for none.</summary>
    public virtual MenuFlyout? Filter()
    {
        return null;
    }

    /// <summary>The window colour changed; the skin's brushes already hold the new colours.</summary>
    public virtual void Paint(Skin s)
    {
    }

    /// <summary>
    /// The section is left or the Library closes: forget work that is still on its way,
    /// so a late answer cannot land in a list nobody looks at.
    /// </summary>
    public virtual void Park()
    {
    }

    /// <summary>Set by LibView: open this address in a new tab.</summary>
    public Action<string>? Opened { get; set; }

    /// <summary>Set by LibView: a bookmark or history entry was removed (counters and the star may need a refresh).</summary>
    public Action? Edited { get; set; }
}
