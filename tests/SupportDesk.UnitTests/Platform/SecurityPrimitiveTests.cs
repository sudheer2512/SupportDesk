using System.Security.Cryptography;
using SupportDesk.Application.Platform;

namespace SupportDesk.UnitTests.Platform;

public sealed class SecurityPrimitiveTests
{
    [Fact]
    public void Totp_matches_rfc6238_reference_values()
    {
        // RFC 6238 SHA-1 secret "12345678901234567890"; 8-digit reference 94287082 at T=59 → 6-digit 287082.
        var secret = Totp.Base32Encode("12345678901234567890"u8.ToArray());
        Assert.Equal("287082", Totp.Compute(secret, DateTime.UnixEpoch.AddSeconds(59)));
        Assert.Equal("081804", Totp.Compute(secret, DateTime.UnixEpoch.AddSeconds(1111111109)));
        Assert.True(Totp.Verify(secret, "287 082", DateTime.UnixEpoch.AddSeconds(59 + 25)));
        Assert.False(Totp.Verify(secret, "287082", DateTime.UnixEpoch.AddSeconds(59 + 120)));
    }

    [Fact]
    public void Base32_round_trip()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        Assert.Equal(bytes, Totp.Base32Decode(Totp.Base32Encode(bytes)));
    }

    [Fact]
    public void Secret_protector_round_trips_and_detects_tampering()
    {
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var sealedValue = protector.Protect("imap-password-123");
        Assert.StartsWith("v1:", sealedValue);
        Assert.NotEqual(sealedValue, protector.Protect("imap-password-123")); // random nonce
        Assert.Equal("imap-password-123", protector.Unprotect(sealedValue));
        var bytes = Convert.FromBase64String(sealedValue[3..]);
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1:" + Convert.ToBase64String(bytes)));
        var other = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(sealedValue));
    }

    [Fact]
    public void Html_sanitizer_removes_active_content_but_keeps_formatting()
    {
        var html = new HtmlSanitization();
        var clean = html.Sanitize("""
            <p onclick="steal()">Hi <b>there</b> <a href="javascript:alert(1)">x</a> <a href="https://wings.demo/kb">kb</a></p>
            <script>alert(1)</script><img src="x" onerror="alert(1)"><iframe src="https://evil.example/x"></iframe>
            <iframe src="https://www.youtube.com/embed/abc123"></iframe><table><tr><td>cell</td></tr></table>
            """);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", clean);
        Assert.DoesNotContain("onerror", clean);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("javascript:", clean);
        Assert.DoesNotContain("evil.example", clean);
        Assert.Contains("youtube.com/embed/abc123", clean);
        Assert.Contains("<b>there</b>", clean);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", clean);
        Assert.Contains("<td>cell</td>", clean);
    }

    [Fact]
    public void Password_policy_enforces_complexity_and_history()
    {
        var service = new PasswordService();
        var policy = new PasswordPolicy { MinLength = 10, HistoryCount = 3 };
        Assert.NotEmpty(service.Validate("short1A", policy));
        Assert.NotEmpty(service.Validate("alllowercase123", policy));
        var oldHash = service.Hash("Previous#Pass1");
        Assert.Contains(service.Validate("Previous#Pass1", policy, [oldHash]), e => e.Contains("reuse"));
        Assert.Empty(service.Validate("Brand-New-Pass9", policy, [oldHash]));
        Assert.True(service.Verify(service.Hash("Brand-New-Pass9"), "Brand-New-Pass9"));
    }

    [Fact]
    public void File_names_are_sanitised() =>
        Assert.Equal("evil.txt", FileService.SanitizeFileName("..\\..\\windows/evil.txt"));
}
