using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Application.Platform;

namespace SupportDesk.Application.Identity;

/// <summary>
/// ES256 signing key for access tokens. Persisted encrypted (master key) under data/keys so tokens survive restarts and
/// all API instances sharing the data path / configuration validate each other's tokens. The public key is published as JWKS.
/// </summary>
public sealed class SigningKeyProvider
{
    public const string Issuer = "supportdesk";
    public const string Audience = "supportdesk-api";

    public ECDsaSecurityKey Key { get; }
    public SigningCredentials Credentials { get; }

    public SigningKeyProvider(IConfiguration config, IHostEnvironment env, ISecretProtector protector)
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configured = config["Security:JwtSigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ec.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(configured)), out _);
        }
        else
        {
            var path = Path.Combine(DataPaths.Root(config, env), "keys", "jwt-signing.key");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(File.ReadAllText(path).Trim())), out _);
            }
            else
            {
                File.WriteAllText(path, protector.Protect(Convert.ToBase64String(ec.ExportPkcs8PrivateKey())));
            }
        }
        var pub = ec.ExportParameters(false);
        var kid = Base64UrlEncoder.Encode(SHA256.HashData([.. pub.Q.X!, .. pub.Q.Y!]))[..16];
        Key = new ECDsaSecurityKey(ec) { KeyId = kid };
        Credentials = new SigningCredentials(Key, SecurityAlgorithms.EcdsaSha256);
    }

    public object Jwks()
    {
        var p = Key.ECDsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new { kty = "EC", use = "sig", crv = "P-256", kid = Key.KeyId, alg = "ES256", x = Base64UrlEncoder.Encode(p.Q.X), y = Base64UrlEncoder.Encode(p.Q.Y) }
            }
        };
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
        RoleClaimType = "role"
    };
}

public static class DeskClaims
{
    public const string Subject = "sub";
    public const string Tenant = "tid";
    public const string TenantSlug = "tslug";
    public const string Type = "typ";
    public const string Session = "sid";
    public const string Name = "name";
    public const string Email = "email";
    public const string Scope = "scope";
    public const string Brand = "brand";
    public const string AuthMethods = "amr";
    /// <summary>Per-credential API rate limit (requests per minute) of an API key or OAuth client.</summary>
    public const string RateLimit = "rlm";

    public const string TypeAgent = "agent";
    public const string TypeContact = "contact";
    public const string TypePlatform = "platform";
    public const string TypeClient = "client";
}

public sealed record IssuedToken(string Token, DateTime ExpiresAt);

public sealed class TokenService(SigningKeyProvider keys, Domain.IClock clock)
{
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public IssuedToken Issue(string type, Guid subject, Guid? tenantId, string? tenantSlug, string name, TimeSpan lifetime,
        Guid? sessionId = null, string? email = null, IEnumerable<string>? scopes = null, string? brand = null, bool mfa = false,
        int? rateLimitPerMinute = null)
    {
        var now = clock.UtcNow;
        var claims = new List<Claim>
        {
            new(DeskClaims.Subject, subject.ToString()),
            new(DeskClaims.Type, type),
            new(DeskClaims.Name, name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (tenantId.HasValue) claims.Add(new Claim(DeskClaims.Tenant, tenantId.Value.ToString()));
        if (tenantSlug is not null) claims.Add(new Claim(DeskClaims.TenantSlug, tenantSlug));
        if (sessionId.HasValue) claims.Add(new Claim(DeskClaims.Session, sessionId.Value.ToString()));
        if (email is not null) claims.Add(new Claim(DeskClaims.Email, email));
        if (scopes is not null) claims.Add(new Claim(DeskClaims.Scope, string.Join(' ', scopes)));
        if (brand is not null) claims.Add(new Claim(DeskClaims.Brand, brand));
        if (rateLimitPerMinute is > 0) claims.Add(new Claim(DeskClaims.RateLimit, rateLimitPerMinute.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        claims.Add(new Claim(DeskClaims.AuthMethods, mfa ? "mfa" : "pwd"));
        var expires = now.Add(lifetime);
        var token = new JwtSecurityToken(SigningKeyProvider.Issuer, SigningKeyProvider.Audience, claims, now.AddSeconds(-5), expires, keys.Credentials);
        return new IssuedToken(_handler.WriteToken(token), expires);
    }
}
