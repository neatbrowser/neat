using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>The General page: the home page and the search engine.</summary>
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

        return Block.Page(
            Block.Head(_skin, "Startup"),
            Block.Card(_skin,
                Block.Row(_skin, "Home page", "Opens in the first tab at startup, and with Alt+Home.", _home)),
            Block.Head(_skin, "Search"),
            Block.Card(_skin,
                Block.Row(_skin, "Search engine", "Used for anything typed in the address bar that is not a web address.", box)));
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
