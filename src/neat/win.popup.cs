using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Windows a page opens for itself: window.open() (the sign-in popups of Google, Apple, Microsoft,
/// Firebase and the like) and links that ask for a new window (target=_blank, ctrl+click).
///
/// A page that opens a window keeps a handle to it (the value window.open() returns, and
/// window.opener the other way round), and a sign-in popup ends by posting its result through that
/// handle. So the new tab has to be the very window the page asked for, not a separate tab that is
/// merely sent to the same address. WebView2 does that when the new web view is handed to the event
/// as NewWindow. With Handled alone the page is given a window that never loads (window.open()
/// returned null here), and Google's sign-in then reports "Failed to open popup window".
///
/// Order, from the NewWindowRequested documentation: the scripts that must see the first page are
/// added, and finished, before NewWindow is set; the request filters and the other handlers are
/// added after it; the event is held (a deferral) until all of that is done, and the new window
/// starts loading when the deferral is completed.
/// </summary>
public sealed partial class Win
{
    // The tab a window was opened from, so that window.close() can go back to it.
    private readonly ConditionalWeakTable<Tab, Tab> _opener = new();

    /// <summary>
    /// A page asked for a new window. An ad's popup is refused; any other becomes a new tab that
    /// is the window the page asked for.
    /// </summary>
    private void NewWindowAsked(Tab from, CoreWebView2 core, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Set first: whatever happens next, WebView2 never opens a window of its own as well.
        // Without a NewWindow the page just gets a window that does not load.
        e.Handled = true;

        if (_closed || AdsBlockPopup(from, e.Uri))
            return;

        var d = e.GetDeferral();
        _ = OpenPopup(from, core, e, d);
    }

    /// <summary>Makes the tab for a window a page asked for, hands its web view to the page, and lets the page go on.</summary>
    private async Task OpenPopup(Tab from, CoreWebView2 core, CoreWebView2NewWindowRequestedEventArgs e, Windows.Foundation.Deferral d)
    {
        Tab? tab = null;
        var bound = false;

        try
        {
            var uri = e.Uri;

            tab = new Tab(new WebView2()) { Asked = uri };
            host.Children.Add(tab.View);

            // Clicking into the page puts the Blocker panel away (as in Open).
            tab.View.GotFocus += (s, x) => BlkShow(false);

            _tabs.Add(tab);
            _opener.AddOrUpdate(tab, from);
            Log.Tabs = _tabs.Count;
            Log.Write("tab", $"page asked for a new window, tabs now {_tabs.Count}");
            Pick(tab);

            // The same environment as the page that asks, so the same profile and cookies.
            await tab.View.EnsureCoreWebView2Async(core.Environment);

            var pop = tab.View.CoreWebView2;
            if (pop is null)
            {
                Log.Write("tab", "the new window was closed before its web view finished starting");
                return;
            }

            // 1. The scripts every page gets, finished before NewWindow is set. Each of the three
            //    already catches and logs its own failure.
            try
            {
                await pop.AddScriptToExecuteOnDocumentCreatedAsync(Js());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[keys] script failed: " + ex.Message);
            }

            await AddPassScript(pop);
            await FpRegister(tab, pop);

            // The tab may have been closed while the scripts were being added.
            if (!_tabs.Contains(tab) || tab.View.CoreWebView2 is null)
            {
                Log.Write("tab", "the new window was closed before it could be handed to the page");
                return;
            }

            // 2. This tab's web view is the window the page asked for.
            e.NewWindow = pop;
            bound = true;

            // 3. Then what every tab has: its handlers and request filters.
            Hook(tab, pop);
            HookPass(tab, pop);
            AdsHook(tab, pop);
            FpEvents(tab, pop);

            // The window's first page is its address. Normally NavigationStarting says so itself and
            // replaces this; it is here so the blocker has a page to judge by if that event never comes.
            if (AdUrl.IsWeb(uri))
            {
                var st = AdsOf(tab);
                st.Page = uri;
                st.Nav = uri;
                st.NavPending = true;
            }
        }
        catch (Exception ex)
        {
            // A failure anywhere above ends up here and never leaves the event handler: an exception
            // that escapes one ends the whole app.
            Log.Error("tab", ex, "could not open the window a page asked for");

            // Not handed over: the page gets a window that does not load, so its tab is no use.
            if (!bound && tab is not null)
            {
                var gone = tab;
                DispatcherQueue.TryEnqueue(() => Shut(gone));
            }
        }
        finally
        {
            // Always, or the page waits for its window for ever. The window starts loading now.
            try
            {
                d.Complete();
            }
            catch (Exception ex)
            {
                Log.Error("tab", ex, "could not let the new window go on");
            }
        }
    }
}
