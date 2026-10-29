using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// The fingerprint protection's part of the window. Every tab gets one page script (fp/farble.js,
/// filled in with the settings), which runs in the page and in each of its frames before the
/// page's own scripts and changes what fingerprinting scripts can read. This file registers that
/// script, registers it again when the settings change, and adjusts two request headers
/// (Sec-GPC and Accept-Language) to match. The decisions are in fp/fp.cs.
/// </summary>
public sealed partial class Win
{
    private sealed class FpTab
    {
        /// <summary>Id of the registered script, or null when none is registered.</summary>
        public string? Id;

        /// <summary>Registering and removing never overlap for one tab.</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly ConditionalWeakTable<Tab, FpTab> _fp = new();

    // The work started by the last change of settings; the Blocker panel waits for it before it reloads the page.
    private Task _fpBusy = Task.CompletedTask;

    private bool _fpHeaderFailed;
    private bool _fpLangLogged;

    private void FpInit() => Fp.Changed += FpOnChanged;

    private void FpStop() => Fp.Changed -= FpOnChanged;

    private void FpOnChanged() => _fpBusy = FpReapply();

    /// <summary>Starts the protection for a tab: its script, and the header changes. Called once, before the tab's first page.</summary>
    private async Task FpHook(Tab tab, CoreWebView2 core)
    {
        // The request event is shared with the ad blocker, which adds the filter for every request
        // (AdsHook), so no filter is added here.
        core.WebResourceRequested += (s, e) => FpRequest(tab, e);
        await FpRegister(tab, core);
    }

    /// <summary>Registers the page script again for every tab of this window.</summary>
    private async Task FpReapply()
    {
        try
        {
            foreach (var tab in _tabs.ToList())
            {
                if (tab.View.CoreWebView2 is { } core)
                    await FpRegister(tab, core);
            }
        }
        catch (Exception ex)
        {
            Log.Error("fp", ex, "could not apply the new settings to the tabs");
        }
    }

    /// <summary>
    /// Replaces the tab's page script with one for the current settings. The new script applies from
    /// the tab's next page on; the caller reloads the page it is on.
    /// </summary>
    private async Task FpRegister(Tab tab, CoreWebView2 core)
    {
        var st = _fp.GetValue(tab, _ => new FpTab());
        await st.Gate.WaitAsync();
        try
        {
            if (st.Id is not null)
            {
                try
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(st.Id);
                }
                catch (Exception ex)
                {
                    Log.Write("fp", "could not remove the old page script: " + ex.Message);
                }

                st.Id = null;
            }

            var prefs = App.Cfg.Cur.Fp;
            if (!Fp.Needed(prefs))
                return;

            st.Id = await core.AddScriptToExecuteOnDocumentCreatedAsync(Fp.Build(Fp.Template, Fp.Secret, prefs));
        }
        catch (Exception ex)
        {
            Log.Error("fp", ex, "could not register the fingerprint script");
        }
        finally
        {
            st.Gate.Release();
        }
    }

    /// <summary>Sec-GPC and Accept-Language, on every request of a protected page (the page's own, its frames' and its workers').</summary>
    private void FpRequest(Tab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var prefs = App.Cfg.Cur.Fp;
        if (!prefs.On || !AdUrl.IsWeb(e.Request.Uri))
            return;

        var level = Fp.LevelFor(prefs, AdUrl.Host(AdsOf(tab).Page));
        if (level <= FpLevel.Off)
            return;

        try
        {
            var h = e.Request.Headers;
            if (prefs.Gpc)
                h.SetHeader("Sec-GPC", "1");

            // The engine may add Accept-Language only after this event; then standard has nothing to
            // shorten and leaves it, and strict sets it. The log says once which it is here.
            var has = h.Contains("Accept-Language");
            if (!_fpLangLogged)
            {
                _fpLangLogged = true;
                Log.Write("fp", has ? "Accept-Language is visible in requests" : "Accept-Language is not visible in requests (standard leaves it, strict sets it)");
            }

            var want = Fp.AcceptLanguage(level, has ? h.GetHeader("Accept-Language") : null);
            if (want is not null)
                h.SetHeader("Accept-Language", want);
        }
        catch (Exception ex)
        {
            // Said once: this runs for every request.
            if (!_fpHeaderFailed)
            {
                _fpHeaderFailed = true;
                Log.Write("fp", "could not change the headers of a request: " + ex.Message);
            }
        }
    }
}
