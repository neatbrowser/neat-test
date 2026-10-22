using Microsoft.UI.Xaml;

namespace neat;

/// <summary>
/// The Shortcuts page: a read-only list of the keyboard shortcuts, grouped as Tabs,
/// Navigation, Page and Window. It is built from the same table the shortcuts
/// themselves are registered in (Win.Cmds, see win.keys.cs), so a shortcut that
/// is added or changed there shows up here without anyone editing this file.
/// </summary>
public sealed partial class Opts
{
    private UIElement BuildKeys()
    {
        var parts = new List<UIElement>();

        // The groups come out in the order of the Area enum.
        foreach (var area in Enum.GetValues<Area>())
        {
            // Shortcuts that do the same thing (Ctrl+R and F5) share a name, so they share a row.
            // GroupBy keeps the order the shortcuts were registered in.
            var rows = _own.Cmds
                .Where(c => c.Area == area)
                .GroupBy(c => c.Name)
                .Select(g => (UIElement)Block.Row(_skin, g.Key, null, Block.Value(_skin, Win.Show(g.ToList()))))
                .ToArray();

            if (rows.Length == 0)
                continue;

            parts.Add(Block.Head(_skin, area.ToString()));
            parts.Add(Block.Card(_skin, rows));
        }

        return Block.Page(parts.ToArray());
    }
}
