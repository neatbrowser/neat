using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace neat;

/// <summary>
/// The one-time code (the 6 digits an authenticator app shows) of a login, made from a secret key
/// that the site gave when two-step sign-in was set up (RFC 6238, built on RFC 4226). It needs
/// nothing but the key and the clock, so it works with no network and no account.
///
/// The key is read from what the site shows: the plain key (letters and digits, in groups or not),
/// or the address behind its QR code (otpauth://totp/...?secret=...), which may also say the
/// algorithm (SHA1, SHA256 or SHA512), the number of digits (6 to 8) and the period in seconds.
/// What is kept is one canonical text that <see cref="Parse"/> reads back, so a setting the address
/// gave is not lost. Counter-based keys (otpauth://hotp) are not supported.
///
/// The key is a secret as much as a password is: the vault keeps it sealed (vault.cs), and it is
/// only ever handed to this class for the moment a code is made.
/// </summary>
public sealed class Totp
{
    /// <summary>The longest key accepted, in bytes. A longer one is not a one-time-code key.</summary>
    public const int MaxKeyBytes = 128;

    /// <summary>The shortest key accepted, in bytes (80 bits, the smallest that sites hand out).</summary>
    public const int MinKeyBytes = 10;

    private readonly byte[] _key;

    private Totp(byte[] key, string algorithm, int digits, int period)
    {
        _key = key;
        Algorithm = algorithm;
        Digits = digits;
        Period = period;
    }

    /// <summary>SHA1, SHA256 or SHA512.</summary>
    public string Algorithm { get; }

    /// <summary>How many digits a code has: 6, 7 or 8.</summary>
    public int Digits { get; }

    /// <summary>How many seconds one code lasts.</summary>
    public int Period { get; }

    /// <summary>
    /// Reads a key as the site shows it. Null when it is not one: not base32, too short or too long,
    /// a counter-based key, or settings that make no sense.
    /// </summary>
    public static Totp? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
            return null;

        text = text.Trim();

        var secret = text;
        var algorithm = "SHA1";
        var digits = 6;
        var period = 30;

        if (text.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("otpauth://totp?", StringComparison.OrdinalIgnoreCase))
                return null;

            var query = text.IndexOf('?');
            if (query < 0)
                return null;

            secret = string.Empty;

            foreach (var pair in text[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;

                var name = pair[..eq].ToLowerInvariant();
                string value;
                try
                {
                    value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return null;
                }

                switch (name)
                {
                    case "secret":
                        secret = value;
                        break;

                    case "algorithm":
                        algorithm = value.ToUpperInvariant();
                        break;

                    case "digits":
                        if (!int.TryParse(value, out digits))
                            return null;

                        break;

                    case "period":
                        if (!int.TryParse(value, out period))
                            return null;

                        break;
                }
            }
        }

        if (algorithm != "SHA1" && algorithm != "SHA256" && algorithm != "SHA512")
            return null;

        if (digits < 6 || digits > 8 || period < 10 || period > 300)
            return null;

        var key = FromBase32(secret);
        if (key is null || key.Length < MinKeyBytes || key.Length > MaxKeyBytes)
            return null;

        return new Totp(key, algorithm, digits, period);
    }

    /// <summary>The text the vault keeps: an otpauth address with every setting spelled out.</summary>
    public string Canonical()
    {
        return "otpauth://totp/neat?secret=" + ToBase32(_key) + "&algorithm=" + Algorithm + "&digits=" + Digits + "&period=" + Period;
    }

    /// <summary>The code for a moment, with leading zeros kept. Empty for a moment before 1970.</summary>
    public string Code(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        if (seconds < 0)
            return string.Empty;

        var message = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(message, (ulong)(seconds / Period));

        var mac = Algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(_key, message),
            "SHA512" => HMACSHA512.HashData(_key, message),
            _ => HMACSHA1.HashData(_key, message),
        };

        var at = mac[^1] & 0x0f;
        var number = ((mac[at] & 0x7f) << 24) | (mac[at + 1] << 16) | (mac[at + 2] << 8) | mac[at + 3];

        var modulus = 1;
        for (var i = 0; i < Digits; i++)
            modulus *= 10;

        return (number % modulus).ToString().PadLeft(Digits, '0');
    }

    /// <summary>How many seconds the code of that moment still lasts (1 to <see cref="Period"/>).</summary>
    public int SecondsLeft(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        return seconds < 0 ? Period : Period - (int)(seconds % Period);
    }

    // ---- base32 (RFC 4648), as sites write it: any case, with spaces, hyphens or = padding ----

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static byte[]? FromBase32(string text)
    {
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();

        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=' or '\t' or '\r' or '\n')
                continue;

            var at = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (at < 0)
                return null;

            buffer = (buffer << 5) | at;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
                buffer &= (1 << bits) - 1;

                if (output.Count > MaxKeyBytes)
                    return null;
            }
        }

        return output.ToArray();
    }

    internal static string ToBase32(byte[] bytes)
    {
        var sb = new StringBuilder();
        var bits = 0;
        var buffer = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
                buffer &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return sb.ToString();
    }
}
