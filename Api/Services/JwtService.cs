using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public sealed record AuthContext(string Principal, string Consumer, string[] Scopes);

public sealed class JwtService
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _key;

    public JwtService(IConfiguration cfg)
    {
        _issuer = cfg["COURSE_JWT_ISSUER"] ?? "moduledev-course";
        _audience = cfg["COURSE_JWT_AUDIENCE"] ?? "moduledev-api";
        _key = cfg["COURSE_JWT_SIGNING_KEY"] ?? new string('x', 32);
    }

    public bool TryValidate(string? authHeader, out JsonElement claims)
    {
        claims = default;
        
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        
        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token)) 
            return false;

        var handler = new JwtSecurityTokenHandler();
        var parms = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
        };
        try
        {
            var rawClaims = ReadRawPayload(token);
            if (!IsValidClaimShape(rawClaims, _issuer, _audience, out var parsed))
                return false;
            handler.ValidateToken(token, parms, out _);
            claims = parsed;
            return true;
        }
        catch (Exception ex) when (
            ex is SecurityTokenException
            || ex is ArgumentException
            || ex is FormatException
            || ex is JsonException)
        {
            return false;
        }
    }

    private static JsonElement ReadRawPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new FormatException("token must have three segments");
        var json = Base64UrlEncoder.Decode(parts[1]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool IsValidClaimShape(JsonElement claims, string issuer, string audience, out JsonElement parsed)
    {
        parsed = default;
        if (!claims.TryGetProperty("iss", out var iss)
            || iss.ValueKind != JsonValueKind.String
            || iss.GetString() != issuer)
            return false;
        if (!claims.TryGetProperty("aud", out var aud)
            || aud.ValueKind != JsonValueKind.String
            || aud.GetString() != audience)
            return false;
        if (!claims.TryGetProperty("sub", out var sub)
            || sub.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(sub.GetString()))
            return false;
        if (!claims.TryGetProperty("consumer", out var cons)
            || cons.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(cons.GetString()))
            return false;
        if (!claims.TryGetProperty("scope", out var sc)
            || sc.ValueKind != JsonValueKind.String)
            return false;
        if (!claims.TryGetProperty("iat", out var iat)
            || iat.ValueKind != JsonValueKind.Number
            || !iat.TryGetInt64(out _))
            return false;
        if (!claims.TryGetProperty("exp", out var exp)
            || exp.ValueKind != JsonValueKind.Number
            || !exp.TryGetInt64(out _))
            return false;

        parsed = claims;
        return true;
    }

    public static bool TryGetAuthContext(JsonElement claims, out AuthContext context)
    {
        context = null!;

        if (!claims.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String) 
            return false;
        if (!claims.TryGetProperty("consumer", out var consumer) || consumer.ValueKind != JsonValueKind.String) 
            return false;
        if (!claims.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String) 
            return false;

        context = new AuthContext(
            sub.GetString()!,
            consumer.GetString()!,
            scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        
        return true;
    }
}
