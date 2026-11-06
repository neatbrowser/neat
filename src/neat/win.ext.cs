namespace neat;

/// <summary>
/// The window's part of extensions: it tells <see cref="Ext"/> where to get a profile. The rest of
/// the extension code (web/ext.cs, web/extprof.cs) knows nothing of tabs.
/// </summary>
public sealed partial class Win
{
    // Kept so ExtStop can tell whether the resolver is still ours (another window may have set its own).
    private Func<IExtProf?>? _extSrc;

    private void ExtInit()
    {
        _extSrc = ExtProfile;
        Ext.Src = _extSrc;
    }

    private void ExtStop()
    {
        if (Ext.Src == _extSrc)
            Ext.Src = null;
    }

    /// <summary>
    /// The profile of a tab that is alive. All tabs share one profile, so any tab will do; the
    /// current tab goes first. Asked for on every call and never kept, because a profile object
    /// belongs to its web view and fails once that tab is closed.
    /// </summary>
    private IExtProf? ExtProfile()
    {
        if (_closed)
            return null;

        var first = _cur is null ? Array.Empty<Tab>() : new[] { _cur };
        foreach (var t in first.Concat(_tabs))
        {
            try
            {
                if (t.View.CoreWebView2 is { } core)
                    return new ExtProf(core.Profile);
            }
            catch (Exception ex)
            {
                // A tab that is shutting down; the next one may be fine.
                Log.Error("ext", ex, "tab has no usable profile");
            }
        }

        return null;
    }
}
