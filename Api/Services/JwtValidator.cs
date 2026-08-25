using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public sealed class JwtValidator
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _key;

    public JwtValidator(IConfiguration cfg)
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
            RequireSignedTokens = true
        };
        try
        {
            handler.ValidateToken(token, parms, out _);
            var jwt = handler.ReadJwtToken(token);
            claims = JsonDocument.Parse(jwt.Payload.SerializeToJson()).RootElement;

            if (!claims.TryGetProperty("iss", out var iss) || iss.ValueKind != JsonValueKind.String || iss.GetString() != _issuer) return false;
            if (!claims.TryGetProperty("aud", out var aud) || aud.ValueKind != JsonValueKind.String || aud.GetString() != _audience) return false;
            if (!claims.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(sub.GetString())) return false;
            if (!claims.TryGetProperty("consumer", out var cons) || cons.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cons.GetString())) return false;
            if (!claims.TryGetProperty("scope", out var sc) || sc.ValueKind != JsonValueKind.String) return false;
            if (!claims.TryGetProperty("iat", out var iat) || iat.ValueKind != JsonValueKind.Number || !iat.TryGetInt64(out _)) return false;
            if (!claims.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out _)) return false;

            return true;
        }
        catch (Exception ex) when (ex is SecurityTokenException || ex is ArgumentException)
        {
            return false;
        }
    }
}
