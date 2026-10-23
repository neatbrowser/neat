using neat;

internal static class Program
{
    private static int _failed;
    private static int _ran;

    private static int Main()
    {
        Check("normal session keeps order and active tab", () =>
        {
            var s = Load("""{"Urls":["https://one.example","https://two.example"],"Active":1}""");
            Expect(s.Urls.SequenceEqual(new[] { "https://one.example", "https://two.example" }), "URL order changed");
            Expect(s.Active == 1, "active index changed");
        });

        Check("null URL entries are discarded", () =>
        {
            var s = Load("""{"Urls":["https://one.example",null,"https://two.example"],"Active":2}""");
            Expect(s.Urls.SequenceEqual(new[] { "https://one.example", "https://two.example" }), "null URL survived");
            Expect(s.Urls.All(url => url is not null), "recovered list contains null");
            Expect(s.Active == 1, "active index did not clamp after filtering");
        });

        Check("blank URLs are discarded", () =>
        {
            var s = Load("""{"Urls":["https://one.example","", "  "],"Active":2}""");
            Expect(s.Urls.SequenceEqual(new[] { "https://one.example" }), "blank URL survived");
            Expect(s.Active == 0, "active index did not clamp after filtering");
        });

        Check("null URL list falls back to empty", () =>
        {
            var s = Load("""{"Urls":null,"Active":42}""");
            Expect(s.Urls.Count == 0 && s.Active == 0, "invalid URL list was not repaired");
        });

        Check("malformed JSON falls back to empty", () =>
        {
            var s = Load("{");
            Expect(s.Urls.Count == 0 && s.Active == 0, "malformed session did not fall back");
        });

        Console.WriteLine();
        Console.WriteLine($"{_ran - _failed} of {_ran} checks passed.");
        return _failed == 0 ? 0 : 1;
    }

    private static Snap Load(string json)
    {
        var folder = Path.Combine(Path.GetTempPath(), "neat-session-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.json");
        File.WriteAllText(path, json);
        return new Session(path).Load();
    }

    private static void Check(string name, Action action)
    {
        _ran++;
        try
        {
            action();
            Console.WriteLine("ok    " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name + ": " + ex.Message);
        }
    }

    private static void Expect(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
