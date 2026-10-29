using System.Text.Json;
using System.Text.Json.Nodes;
using neat;

// Self-check of the fingerprint protection's plain-code pieces. Plain asserts, no test package:
// one line per check, exit code 1 if any failed.

internal static class Program
{
    private static int _ran;
    private static int _failed;

    private static int Main()
    {
        Run("a site gets the default level unless listed", DefaultLevel);
        Run("a listed site covers its subdomains, the most specific entry wins", Specific);
        Run("off everywhere when the protection is switched off", MasterOff);
        Run("setting a site's level lists it only when it differs from the default", SetLevel);
        Run("a loaded settings file is put in order", Clean);
        Run("settings survive saving and loading", RoundTrip);
        Run("a page script is only needed when something can be protected", Needed);
        Run("the page script is filled in", Build);
        Run("the real script has one place to fill in", RealTemplate);
        Run("Accept-Language follows the level", AcceptLanguage);
        Run("browser arguments follow the WebRTC setting", Arguments);

        Console.WriteLine(_failed == 0 ? $"{_ran} checks passed" : $"{_failed} of {_ran} checks FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        _ran++;
        try
        {
            check();
            Console.WriteLine("ok   " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void Is<T>(T want, T got, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(want, got))
            throw new Exception($"{what}: wanted {want}, got {got}");
    }

    private static void DefaultLevel()
    {
        var p = new FpPrefs();
        Is(FpLevel.Standard, Fp.LevelFor(p, "example.com"), "default is standard");
        p.Default = FpLevel.Strict;
        Is(FpLevel.Strict, Fp.LevelFor(p, "example.com"), "default changed");
        Is(FpLevel.Off, Fp.LevelFor(p, null), "unknown host");
    }

    private static void Specific()
    {
        var p = new FpPrefs { Sites = new() { ["example.com"] = FpLevel.Off, ["shop.example.com"] = FpLevel.Strict } };
        Is(FpLevel.Off, Fp.LevelFor(p, "example.com"), "the site");
        Is(FpLevel.Off, Fp.LevelFor(p, "www.example.com"), "a subdomain");
        Is(FpLevel.Strict, Fp.LevelFor(p, "shop.example.com"), "the more specific entry");
        Is(FpLevel.Strict, Fp.LevelFor(p, "a.shop.example.com"), "below the more specific entry");
        Is(FpLevel.Standard, Fp.LevelFor(p, "notexample.com"), "a host that only ends the same way is not covered");
    }

    private static void MasterOff()
    {
        var p = new FpPrefs { On = false, Sites = new() { ["example.com"] = FpLevel.Strict } };
        Is(FpLevel.Off, Fp.LevelFor(p, "example.com"), "listed site while off");
        Is(FpLevel.Off, Fp.LevelFor(p, "other.org"), "other site while off");
    }

    private static void SetLevel()
    {
        var p = new FpPrefs();
        var before = p.Sites;

        Fp.SetLevel(p, "www.example.com", FpLevel.Off);
        Is(true, ReferenceEquals(before, p.Sites) == false, "a new dictionary is stored");
        Is(1, p.Sites.Count, "one entry");
        Is(true, p.Sites.ContainsKey("example.com"), "listed without www.");
        Is(FpLevel.Off, Fp.LevelFor(p, "blog.example.com"), "covers the site's other subdomains");

        Fp.SetLevel(p, "blog.example.com", FpLevel.Strict);
        Is(0, p.Sites.Count(kv => kv.Key == "example.com"), "the entry that covered the host is gone");
        Is(FpLevel.Strict, Fp.LevelFor(p, "blog.example.com"), "new level");

        // Back to the default: the site is not listed any more.
        Fp.SetLevel(p, "blog.example.com", FpLevel.Standard);
        Is(0, p.Sites.Count, "nothing listed at the default level");
    }

    private static void Clean()
    {
        var p = new FpPrefs
        {
            Default = 9,
            Sites = new()
            {
                ["Example.COM"] = 7,
                [".dotted.org."] = -3,
                ["bad host!"] = 1,
                [""] = 1,
                ["ok-site.net"] = 2,
            },
        };

        p.Clean();
        Is(FpLevel.Strict, p.Default, "default clamped");
        Is(3, p.Sites.Count, "junk dropped");
        Is(FpLevel.Strict, p.Sites["example.com"], "lower case, clamped");
        Is(FpLevel.Off, p.Sites["dotted.org"], "dots trimmed, clamped");
        Is(FpLevel.Strict, p.Sites["ok-site.net"], "kept");

        var big = new FpPrefs { Sites = Enumerable.Range(0, 5000).ToDictionary(i => $"h{i}.example", _ => 1) };
        big.Clean();
        Is(FpPrefs.MaxSites, big.Sites.Count, "the list is capped");
    }

    private static void RoundTrip()
    {
        var p = new FpPrefs { Default = 2, Gpc = false, WebRtc = false, Sites = new() { ["a.example"] = 0 } };
        var back = JsonSerializer.Deserialize<FpPrefs>(JsonSerializer.Serialize(p))!;
        back.Clean();
        Is(2, back.Default, "default");
        Is(false, back.Gpc, "gpc");
        Is(false, back.WebRtc, "webrtc");
        Is(0, back.Sites["a.example"], "site");

        // An old settings file without these keys gives the defaults.
        var old = JsonSerializer.Deserialize<FpPrefs>("{}")!;
        Is(true, old.On, "on by default");
        Is(FpLevel.Standard, old.Default, "standard by default");
        Is(true, old.Gpc, "gpc by default");
    }

    private static void Needed()
    {
        Is(true, Fp.Needed(new FpPrefs()), "default standard");
        Is(false, Fp.Needed(new FpPrefs { Default = 0 }), "default off, no sites");
        Is(true, Fp.Needed(new FpPrefs { Default = 0, Sites = new() { ["a.example"] = 1 } }), "one site protected");
        Is(false, Fp.Needed(new FpPrefs { Default = 0, Sites = new() { ["a.example"] = 0 } }), "only an off entry");
        Is(false, Fp.Needed(new FpPrefs { On = false }), "switched off");
    }

    private const string Tpl = "const CFG = __NEAT_FP__; run(CFG);";

    private static void Build()
    {
        var p = new FpPrefs { Default = 1, Gpc = true, Sites = new() { ["a.example"] = 2, ["b.example"] = 0 } };
        var js = Fp.Build(Tpl, "s3cr3t", p);
        Is(false, js.Contains("__NEAT_FP__"), "placeholder filled");

        var json = js["const CFG = ".Length..js.IndexOf("; run(CFG);", StringComparison.Ordinal)];
        var cfg = JsonNode.Parse(json)!.AsObject();
        Is("s3cr3t", (string)cfg["secret"]!, "secret");
        Is(1, (int)cfg["def"]!, "default");
        Is(true, (bool)cfg["gpc"]!, "gpc");
        var sites = cfg["sites"]!.AsArray();
        Is(2, sites.Count, "sites");
        Is(true, sites.Any(s => (string)s![0]! == "a.example" && (int)s![1]! == 2), "a.example strict");
        Is(true, sites.Any(s => (string)s![0]! == "b.example" && (int)s![1]! == 0), "b.example off");

        // Switched off: the script protects nothing.
        var off = JsonNode.Parse(Fp.Build(Tpl, "x", new FpPrefs { On = false, Sites = new() { ["a.example"] = 2 } })
            ["const CFG = ".Length..].Split(';')[0])!.AsObject();
        Is(0, (int)off["def"]!, "default while off");
        Is(0, off["sites"]!.AsArray().Count, "no sites while off");
    }

    private static void RealTemplate()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "neat", "fp", "farble.js");
            if (File.Exists(f))
            {
                file = f;
                break;
            }
        }

        if (file is null)
            throw new Exception("src/neat/fp/farble.js not found above " + dir);

        var text = File.ReadAllText(file);
        var count = text.Split("__NEAT_FP__").Length - 1;
        Is(1, count, "places to fill in");
    }

    private static void AcceptLanguage()
    {
        Is(null, Fp.AcceptLanguage(FpLevel.Off, "vi-VN,vi;q=0.9"), "off leaves it");
        Is("vi-VN", Fp.AcceptLanguage(FpLevel.Standard, "vi-VN,vi;q=0.9,en-US;q=0.8"), "standard keeps the first");
        Is("en", Fp.AcceptLanguage(FpLevel.Standard, "en;q=0.5"), "a single language with a weight");
        Is(null, Fp.AcceptLanguage(FpLevel.Standard, ""), "nothing to keep");
        Is("en-US,en;q=0.9", Fp.AcceptLanguage(FpLevel.Strict, "vi-VN"), "strict says English");
    }

    private static void Arguments()
    {
        Is("--force-webrtc-ip-handling-policy=default_public_interface_only", Fp.BrowserArguments(new FpPrefs()), "on");
        Is(string.Empty, Fp.BrowserArguments(new FpPrefs { WebRtc = false }), "webrtc off");
        Is(string.Empty, Fp.BrowserArguments(new FpPrefs { On = false }), "all off");
    }
}
