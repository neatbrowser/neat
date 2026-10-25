using System.Globalization;

namespace neat;

/// <summary>
/// Shows the address a password is about to be filled into in a way that is hard to fool, and says
/// so when it looks like it could be a fake. A fake site often differs from the real one by a
/// letter that looks the same in another alphabet ("apple.com" written with a Cyrillic "a"). The
/// browser keeps such a host in an ASCII form (punycode, starting xn--), and that form never looks
/// like the real one, so it is always shown next to the readable form.
///
/// This cannot tell a real foreign-language site from a fake one (it only knows letters, not
/// sites), so it warns about the unusual and never calls anything safe.
/// </summary>
public static class Lookalike
{
    /// <summary>
    /// The host of an origin as it should be shown: just the host, and for an address with non-English
    /// letters the readable form followed by the ASCII form in brackets. Other kinds of address
    /// (plain names, numbers) are shown as they are.
    /// </summary>
    public static string Shown(string origin)
    {
        var host = HostOf(origin);
        if (host is not null)
        {
            var readable = Decode(host);
            return readable == host ? host : readable + " (" + host + ")";
        }

        // A number (192.168.0.1, [::1]) is shown as it is, still without the scheme; only text that is
        // not an address at all comes back unchanged.
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : origin;
    }

    /// <summary>
    /// A warning for an address that uses letters outside a-z, stronger when one word of the address
    /// mixes alphabets; null for an ordinary address.
    /// </summary>
    public static string? Warning(string origin)
    {
        var host = HostOf(origin);
        if (host is null)
            return null;

        var any = false;

        foreach (var label in host.Split('.'))
        {
            if (!label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                continue;

            any = true;

            string readable;
            try
            {
                readable = new IdnMapping().GetUnicode(label);
            }
            catch (ArgumentException)
            {
                return "This address is not written correctly, which is a sign of a fake site. Do not fill a password here.";
            }

            if (MixesAlphabets(readable))
            {
                return "This address mixes alphabets (for example Latin and Cyrillic letters). That is how fake sites imitate "
                    + "real ones. Only fill a password here if you are sure this is the right site.";
            }
        }

        return any
            ? "This address uses letters outside a-z. If you did not expect that, check it is the site you mean before filling a password."
            : null;
    }

    private static string? HostOf(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.Dns
            ? u.IdnHost
            : null;
    }

    private static string Decode(string host)
    {
        try
        {
            return new IdnMapping().GetUnicode(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }

    /// <summary>True when one word uses Latin letters together with Cyrillic, Greek or Armenian ones.</summary>
    private static bool MixesAlphabets(string label)
    {
        var latin = false;
        var other = false;

        foreach (var c in label)
        {
            if (!char.IsLetter(c))
                continue;

            if (c <= 0x024F || (c >= 0x1E00 && c <= 0x1EFF))
                latin = true;
            else if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x0400 && c <= 0x052F) || (c >= 0x0530 && c <= 0x058F))
                other = true;
        }

        return latin && other;
    }
}
