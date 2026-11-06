using System.Runtime.InteropServices;
using neat;

// Self-check of the extension wrappers (src/neat/web/ext.cs). Plain asserts, no test package: it
// prints one line per check and exits with 1 if any failed, which fails the CI build.
//
// The wrappers reach a profile only through IExtProf, so a fake stands in for it here. What this
// proves is the contract the Settings page will rely on: a fresh profile lists nothing, nothing
// throws, and a failure comes back as a sentence.

internal static class Program
{
    private static int _ran;
    private static int _failed;

    private static int Main()
    {
        Run("a fresh profile lists no extensions", FreshProfile);
        Run("nothing works, and nothing throws, while extensions are off", WhileOff);
        Run("the reason is kept, and cleared when extensions come on", Reason);
        Run("without a live tab there is nothing to list and a clear message", NoTab);
        Run("add, switch and remove an extension", AddSwitchRemove);
        Run("an unknown id is reported, not thrown", UnknownId);
        Run("a failing add comes back as a sentence", FailingAdd);
        Run("a failing list is an empty list", FailingList);
        Run("a resolver that throws is the same as no tab", ThrowingResolver);
        Run("an empty folder name is refused", EmptyFolder);

        Console.WriteLine($"{_ran - _failed} of {_ran} checks passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<Task> body)
    {
        _ran++;
        try
        {
            // The state is static, so every check starts from the same one.
            Ext.Set(true, null);
            Ext.Src = null;
            body().GetAwaiter().GetResult();
            Console.WriteLine("ok    " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name + ": " + ex.Message);
        }
    }

    private static void Need(bool ok, string what)
    {
        if (!ok)
            throw new Exception(what);
    }

    private static async Task FreshProfile()
    {
        Ext.Src = () => new Fake();
        var all = await Ext.All();
        Need(all.Count == 0, "expected an empty list, got " + all.Count);
    }

    private static async Task WhileOff()
    {
        Ext.Set(false, "off for the check");
        Ext.Src = () => new Fake();

        Need((await Ext.All()).Count == 0, "list should be empty while off");

        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err == "off for the check", "add should say why it cannot");

        var on = await Ext.Enable("a", true);
        var gone = await Ext.Remove("a");
        Need(!on.Ok && !gone.Ok, "enable and remove should fail while off");
    }

    private static Task Reason()
    {
        Ext.Set(false, null);
        Need(!Ext.Can && Ext.Why.Length > 0, "a reason is needed even when none was given");

        Ext.Set(false, "because");
        Need(Ext.Why == "because", "the given reason should be kept");

        Ext.Set(true, "ignored");
        Need(Ext.Can && Ext.Why == "", "the reason should be cleared when extensions are on");
        return Task.CompletedTask;
    }

    private static async Task NoTab()
    {
        Need((await Ext.All()).Count == 0, "no tab means an empty list");

        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && !string.IsNullOrEmpty(add.Err), "add without a tab should explain");
    }

    private static async Task AddSwitchRemove()
    {
        var fake = new Fake();
        Ext.Src = () => fake;

        var add = await Ext.Add(@"C:\ext\one");
        Need(add.Ok && add.Item is not null && add.Item.Name == "one" && add.Item.On, "added extension should be on");

        var all = await Ext.All();
        Need(all.Count == 1 && all[0].Id == add.Item!.Id, "the list should hold the new extension");

        var off = await Ext.Enable(add.Item!.Id, false);
        Need(off.Ok && !(await Ext.All())[0].On, "extension should be off after switching");

        var gone = await Ext.Remove(add.Item!.Id);
        Need(gone.Ok && (await Ext.All()).Count == 0, "extension should be gone after removing");
    }

    private static async Task UnknownId()
    {
        Ext.Src = () => new Fake();

        var on = await Ext.Enable("nope", true);
        var gone = await Ext.Remove("nope");
        Need(!on.Ok && !gone.Ok && on.Err is not null, "an unknown id should be an error result");
    }

    private static async Task FailingAdd()
    {
        // The HRESULT that WebView2 gives for a folder without a manifest.
        Ext.Src = () => new Fake { Boom = new COMException("x", unchecked((int)0x80070002)) };
        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err!.Contains("manifest.json"), "a missing manifest should be named: " + add.Err);

        Ext.Src = () => new Fake { Boom = new InvalidOperationException("odd") };
        add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err == "The extension could not be added.", "an unknown failure gets the plain sentence");
    }

    private static async Task FailingList()
    {
        Ext.Src = () => new Fake { Boom = new InvalidOperationException("closed") };
        Need((await Ext.All()).Count == 0, "a failing list should be empty");
    }

    private static async Task ThrowingResolver()
    {
        Ext.Src = () => throw new ObjectDisposedException("tab");
        Need((await Ext.All()).Count == 0, "list should be empty");
        Need(!(await Ext.Add(@"C:\x")).Ok, "add should fail without throwing");
    }

    private static async Task EmptyFolder()
    {
        Ext.Src = () => new Fake();
        Need(!(await Ext.Add("  ")).Ok, "a blank folder name should be refused");
    }

    // A profile in memory. Boom, when set, is thrown by every call.
    private sealed class Fake : IExtProf
    {
        private readonly List<ExtItem> _all = new();

        public Exception? Boom;

        public Task<List<ExtItem>> All()
        {
            if (Boom is not null)
                throw Boom;
            return Task.FromResult(_all.ToList());
        }

        public Task<ExtItem> Add(string folder)
        {
            if (Boom is not null)
                throw Boom;
            var x = new ExtItem { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(folder.Replace('\\', '/')), On = true };
            _all.Add(x);
            return Task.FromResult(x);
        }

        public Task<bool> Enable(string id, bool on)
        {
            if (Boom is not null)
                throw Boom;
            var i = _all.FindIndex(x => x.Id == id);
            if (i < 0)
                return Task.FromResult(false);
            _all[i] = new ExtItem { Id = id, Name = _all[i].Name, On = on };
            return Task.FromResult(true);
        }

        public Task<bool> Remove(string id)
        {
            if (Boom is not null)
                throw Boom;
            return Task.FromResult(_all.RemoveAll(x => x.Id == id) > 0);
        }
    }
}
