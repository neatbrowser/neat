namespace neat;

/// <summary>
/// What the network says about a login that is being held (see <see cref="Capture"/>). The page
/// script reports a login when the person presses Sign in; whether it worked is found out from the
/// next page. A server that answers the login request with "wrong password" or "too many tries"
/// says so earlier and more plainly, so the held login is dropped at once instead of waiting for the
/// page to show it. This is only ever used to DROP a login: a good answer proves nothing (a site may
/// answer 200 and show an error), so offering still waits for the page, as before.
///
/// The window's own part is one event handler (win.pass.cs); everything that decides is here, so
/// it can be checked without WinUI.
///
/// Limits, on purpose: the request body is not read (WebView2 does not promise it is there, and a
/// site may send a hash, not the password); a request counts only if it is a change-making one (not
/// GET, HEAD or OPTIONS), goes to a host of the same site as the page, and is not an obvious
/// analytics or logging call. "Same site" is the same host, a parent or subdomain of it, or a host
/// with the same parent (api.example.com and accounts.example.com). There is no public-suffix list
/// here, so two unrelated sites under one suffix such as co.uk are taken for related: the cost is
/// that a failing request of one could drop a login held for the other, which is then simply not offered.
/// </summary>
internal static class LoginNet
{
    // Answers that mean "this login was refused". Not 404 and not the 5xx family: analytics and
    // other background calls fail that way often, and a login that hit a server error was not
    // accepted but is also not worth a decision on a guess.
    private static readonly HashSet<int> Refused = new() { 400, 401, 403, 422, 429 };

    // Calls that are about the page's own bookkeeping, not about signing in. Words that may be part
    // of a host name, and whole path segments (a segment must equal one: "/login" is not "/log").
    private static readonly string[] NoiseHost = { "analytics", "telemetry", "sentry", "tracking", "metrics" };
    private static readonly HashSet<string> NoiseSegment = new(StringComparer.Ordinal)
    {
        "analytics", "telemetry", "beacon", "collect", "track", "tracking", "metrics", "log", "logs", "logging", "stats", "csp-report",
    };

    /// <summary>Whether an answer to a request means the held login was refused.</summary>
    /// <param name="site">The origin the login is held for.</param>
    /// <param name="method">The request's method.</param>
    /// <param name="url">The request's address.</param>
    /// <param name="status">The HTTP status of the answer.</param>
    public static bool Refuses(string site, string? method, string? url, int status)
    {
        if (!Refused.Contains(status) || !Changes(method) || !Related(site, url))
            return false;

        return !IsNoise(url);
    }

    private static bool IsNoise(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;

        var host = u.IdnHost.ToLowerInvariant();
        foreach (var word in NoiseHost)
        {
            if (host.Contains(word, StringComparison.Ordinal))
                return true;
        }

        foreach (var segment in u.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NoiseSegment.Contains(segment))
                return true;
        }

        return false;
    }

    /// <summary>A request that changes something on the server: anything but GET, HEAD and OPTIONS.</summary>
    internal static bool Changes(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return false;

        var m = method.Trim().ToUpperInvariant();
        return m != "GET" && m != "HEAD" && m != "OPTIONS";
    }

    /// <summary>Whether a request address is for the same site as an origin (see the limits above).</summary>
    internal static bool Related(string site, string? url)
    {
        var a = HostOf(site);
        var b = HostOf(url);

        if (a is null || b is null)
            return false;

        if (a == b)
            return true;

        // An address is an exact match only, never a "similar" one.
        if (IsAddress(a) || IsAddress(b))
            return false;

        return a.EndsWith("." + b, StringComparison.Ordinal)
            || b.EndsWith("." + a, StringComparison.Ordinal)
            || Parent(a) == Parent(b);
    }

    private static string? HostOf(string? url)
    {
        var origin = Origin.Of(url);
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var u))
            return null;

        return u.IdnHost.Trim('[', ']').ToLowerInvariant();
    }

    private static bool IsAddress(string host)
    {
        return System.Net.IPAddress.TryParse(host, out _);
    }

    // The host without its first label, when that leaves at least two labels: api.example.com is
    // example.com, and example.com stays as it is.
    private static string Parent(string host)
    {
        var first = host.IndexOf('.');
        return first > 0 && host.IndexOf('.', first + 1) > 0 ? host[(first + 1)..] : host;
    }
}
