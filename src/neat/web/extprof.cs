using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// The real <see cref="IExtProf"/>: the extension calls of one CoreWebView2Profile. It turns the
/// WinRT objects into plain <see cref="ExtItem"/> values straight away, so nothing above it ever
/// holds a WebView2 object.
/// </summary>
internal sealed class ExtProf : IExtProf
{
    private readonly CoreWebView2Profile _p;

    public ExtProf(CoreWebView2Profile p) => _p = p;

    public async Task<List<ExtItem>> All()
    {
        var all = new List<ExtItem>();
        foreach (var x in await _p.GetBrowserExtensionsAsync())
            all.Add(Of(x));

        return all;
    }

    public async Task<ExtItem> Add(string folder) => Of(await _p.AddBrowserExtensionAsync(folder));

    public async Task<bool> Enable(string id, bool on)
    {
        var x = await Find(id);
        if (x is null)
            return false;

        await x.EnableAsync(on);
        return true;
    }

    public async Task<bool> Remove(string id)
    {
        var x = await Find(id);
        if (x is null)
            return false;

        await x.RemoveAsync();
        return true;
    }

    // WebView2 has no lookup by id, only the list.
    private async Task<CoreWebView2BrowserExtension?> Find(string id)
    {
        foreach (var x in await _p.GetBrowserExtensionsAsync())
        {
            if (x.Id == id)
                return x;
        }

        return null;
    }

    private static ExtItem Of(CoreWebView2BrowserExtension x) => new() { Id = x.Id, Name = x.Name, On = x.IsEnabled };
}
