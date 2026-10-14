using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SupportDesk.Application.Platform;

/// <summary>
/// AES-256-GCM secret protection. The 32-byte master key comes from configuration (Security:MasterKey, base64) or,
/// in development, from data/keys/master.key (generated on first use). Format: "v1:" + base64(nonce|ciphertext|tag).
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(IConfiguration config, IHostEnvironment env, ILogger<AesGcmSecretProtector> logger)
    {
        var configured = config["Security:MasterKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
        }
        else
        {
            var path = Path.Combine(DataPaths.Root(config, env), "keys", "master.key");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                if (env.IsProduction()) throw new InvalidOperationException("Security:MasterKey must be configured in production.");
                File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                logger.LogWarning("Generated a development master key at {Path}. Configure Security:MasterKey in production.", path);
            }
            _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        if (_key.Length != 32) throw new InvalidOperationException("The master key must be 32 bytes.");
    }

    public AesGcmSecretProtector(byte[] key) => _key = key;

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format.");
        var data = Convert.FromBase64String(protectedValue[3..]);
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(data.Length - 16);
        var cipher = data.AsSpan(12, data.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Derives a purpose-specific key (e.g. JWT signing) from the master key with HKDF.</summary>
    public byte[] DeriveKey(string purpose, int length = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, length, info: Encoding.UTF8.GetBytes(purpose));
}

/// <summary>
/// Server-side HTML sanitisation for all rich text (replies, notes, articles, templates, community posts).
/// Allows formatting, tables, images (http/https/cid/data:image) and YouTube/Vimeo embeds; strips scripts,
/// event handlers, forms and unknown iframes; forces safe link targets.
/// </summary>
public sealed partial class HtmlSanitization
{
    private readonly HtmlSanitizer _sanitizer;

    public HtmlSanitization()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        foreach (var t in new[]
                 {
                     "p", "br", "b", "strong", "i", "em", "u", "s", "strike", "del", "ins", "mark", "small", "sub", "sup", "a", "ul", "ol",
                     "li", "blockquote", "pre", "code", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "table", "thead", "tbody", "tfoot", "tr", "th",
                     "td", "caption", "colgroup", "col", "img", "span", "div", "figure", "figcaption", "iframe", "details", "summary", "kbd"
                 }) _sanitizer.AllowedTags.Add(t);
        _sanitizer.AllowedAttributes.Clear();
        foreach (var a in new[]
                 {
                     "href", "src", "alt", "title", "colspan", "rowspan", "width", "height", "class", "data-mention-id", "data-type",
                     "start", "type", "allowfullscreen", "frameborder", "target", "rel", "align", "valign", "style", "data-language", "open"
                 }) _sanitizer.AllowedAttributes.Add(a);
        _sanitizer.AllowedCssProperties.Clear();
        foreach (var c in new[] { "color", "background-color", "text-align", "font-weight", "font-style", "text-decoration", "width", "height", "border", "padding" })
            _sanitizer.AllowedCssProperties.Add(c);
        _sanitizer.AllowedSchemes.Clear();
        foreach (var s in new[] { "http", "https", "mailto", "tel", "cid", "data" }) _sanitizer.AllowedSchemes.Add(s);
        _sanitizer.AllowDataAttributes = false;

        _sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !DataImageRegex().IsMatch(e.OriginalUrl))
                e.SanitizedUrl = null;
        };
        _sanitizer.RemovingTag += (_, _) => { };
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement el)
            {
                if (el.TagName.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    el.SetAttribute("rel", "noopener noreferrer nofollow");
                    el.SetAttribute("target", "_blank");
                }
            }
        };
        // Only YouTube/Vimeo embeds survive; they are sandboxed. (Done on the whole DOM: removing nodes per-node is ignored.)
        _sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var frame in e.Document.QuerySelectorAll("iframe").ToList())
            {
                var src = frame.GetAttribute("src") ?? "";
                if (!EmbedRegex().IsMatch(src)) frame.Remove();
                else frame.SetAttribute("sandbox", "allow-scripts allow-same-origin allow-presentation");
            }
        };
    }

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? "" : _sanitizer.Sanitize(html);

    [GeneratedRegex(@"^data:image/(png|jpe?g|gif|webp);base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataImageRegex();

    [GeneratedRegex(@"^https://(www\.)?(youtube(-nocookie)?\.com/embed/|player\.vimeo\.com/video/)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedRegex();
}

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) for agent MFA.</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static string OtpAuthUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    public static string Compute(string secret, DateTime utc)
    {
        var counter = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / 30);
        return Compute(Base32Decode(secret), counter);
    }

    public static bool Verify(string secret, string code, DateTime utc, int window = 1)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return false;
        var key = Base32Decode(secret);
        var counter = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / 30);
        for (var i = -window; i <= window; i++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, counter + i)), Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        var bytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        var hash = HMACSHA1.HashData(key, bytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        var clean = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. output];
    }
}

/// <summary>Password hashing (ASP.NET Core Identity PBKDF2 v3 format) and policy validation.</summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object Subject = new();

    public string Hash(string password) => _hasher.HashPassword(Subject, password);

    public bool Verify(string? hash, string password) =>
        !string.IsNullOrEmpty(hash) && _hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<object>().HashPassword(Subject, Guid.NewGuid().ToString()));

    /// <summary>Spends the same time as a real verification (unknown users) so login timing does not reveal accounts.</summary>
    public void VerifyDummy(string password) => Verify(DummyHash.Value, password);

    public IReadOnlyList<string> Validate(string password, PasswordPolicy policy, IEnumerable<string>? previousHashes = null)
    {
        var errors = new List<string>();
        if (password.Length < policy.MinLength) errors.Add($"Use at least {policy.MinLength} characters.");
        if (policy.RequireUpper && !password.Any(char.IsUpper)) errors.Add("Include an upper-case letter.");
        if (policy.RequireLower && !password.Any(char.IsLower)) errors.Add("Include a lower-case letter.");
        if (policy.RequireDigit && !password.Any(char.IsDigit)) errors.Add("Include a digit.");
        if (policy.RequireSymbol && password.All(char.IsLetterOrDigit)) errors.Add("Include a symbol.");
        if (previousHashes is not null && previousHashes.Take(policy.HistoryCount).Any(h => Verify(h, password)))
            errors.Add($"Do not reuse any of your last {policy.HistoryCount} passwords.");
        return errors;
    }
}

/// <summary>Resolves the writable data directory (keys, files, mail outbox): Storage:DataPath or {ContentRoot}/data.</summary>
public static class DataPaths
{
    public static string Root(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Storage:DataPath"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(env.ContentRootPath, "data") : configured);
    }
}

public static class TokenHasher
{
    /// <summary>SHA-256 hex digest for refresh tokens, API keys, survey and visitor tokens (only hashes are stored).</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
