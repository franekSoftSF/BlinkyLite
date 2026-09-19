using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using BlinkyLite.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BlinkyLite.Server.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const string Issuer = "BlinkyLite";
    public const string Audience = "BlinkyLite";

    /// <summary>Base64, at least 32 bytes. Never in appsettings.json in git - env or a Docker secret.</summary>
    [Required]
    public string SigningKey { get; set; } = "";

    public int LifetimeMinutes { get; set; } = 30;

    public SymmetricSecurityKey Key()
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be base64.");
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException($"Jwt:SigningKey is {bytes.Length} bytes; HS256 needs at least 32.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}

/// <summary>Claim names in the token. Short, stable, and not remapped on the way in.</summary>
public static class TokenClaims
{
    public const string Sid = "sub";
    public const string Upn = "upn";
    public const string Name = "name";
    public const string Role = "role";
}

public sealed class TokenService(JwtOptions options, TimeProvider clock)
{
    private readonly SigningCredentials credentials = new(options.Key(), SecurityAlgorithms.HmacSha256);

    public LoginResponse Issue(CurrentUser user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(options.LifetimeMinutes);

        var claims = new Dictionary<string, object>
        {
            [TokenClaims.Sid] = user.Sid,
            [TokenClaims.Upn] = user.Upn,
            [TokenClaims.Name] = user.DisplayName,
            [TokenClaims.Role] = user.Roles.Select(r => r.ToString()).ToArray(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = JwtOptions.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = credentials,
        });

        return new LoginResponse(token, expires, user);
    }

    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidIssuer = JwtOptions.Issuer,
        ValidAudience = JwtOptions.Audience,
        IssuerSigningKey = options.Key(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        // Thirty minutes is the whole budget; five more of default skew would
        // be a sixth of it.
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = TokenClaims.Upn,
        RoleClaimType = TokenClaims.Role,
    };

    /// <summary>The operator behind a request, as the bl_* functions want it.</summary>
    public static CurrentUser CurrentUser(ClaimsPrincipal principal) => new(
        Upn: principal.FindFirstValue(TokenClaims.Upn) ?? "",
        Sid: principal.FindFirstValue(TokenClaims.Sid) ?? "",
        DisplayName: principal.FindFirstValue(TokenClaims.Name) ?? "",
        Roles: principal.FindAll(TokenClaims.Role)
            .Select(c => Enum.TryParse<Role>(c.Value, out var role) ? role : (Role?)null)
            .OfType<Role>()
            .ToList());
}

/// <summary>AD group SIDs to roles, from the <c>Roles</c> section of appsettings.json (D-13).</summary>
public sealed class RoleMap(IReadOnlyDictionary<Role, string[]> groups)
{
    public const string Section = "Roles";

    public IReadOnlyList<Role> RolesFor(IEnumerable<string> groupSids)
    {
        var member = groupSids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups.Where(g => g.Value.Any(member.Contains)).Select(g => g.Key).Order().ToList();
    }

    public static RoleMap From(IConfiguration configuration)
    {
        var map = new Dictionary<Role, string[]>();
        foreach (var role in Enum.GetValues<Role>())
        {
            var sids = configuration.GetSection($"{Section}:{role}").Get<string[]>() ?? [];
            foreach (var sid in sids)
            {
                // By SID, never by name: renaming a group in AD must not
                // quietly grant or remove a role.
                if (!sid.StartsWith("S-1-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Roles:{role} contains '{sid}', which is not a SID.");
                }
            }

            map[role] = sids;
        }

        if (map.Values.All(v => v.Length == 0))
        {
            throw new InvalidOperationException("Roles maps no AD group to any role; nobody could sign in.");
        }

        // The groups behind Admin and SecurityOfficer are the groups that hold
        // Enrollment Agent rights on the CA (D-17). A configuration with
        // nobody able to issue is either a typo or a role model that belongs
        // on the CA first.
        if (map[Role.Admin].Length == 0 && map[Role.SecurityOfficer].Length == 0)
        {
            throw new InvalidOperationException(
                "Roles:Admin and Roles:SecurityOfficer are both empty; nobody could issue a key. " +
                "Use the AD groups that have Enroll rights on the Enrollment Agent template (docs/04-security.md).");
        }

        return new RoleMap(map);
    }
}

/// <summary>Authorization policies; every endpoint names one (docs/04-security.md).</summary>
public static class Policies
{
    public const string CanIssue = nameof(CanIssue);
    public const string CanList = nameof(CanList);
    public const string CanViewDetails = nameof(CanViewDetails);
    public const string CanRevealPuk = nameof(CanRevealPuk);
    public const string CanRevealMgmtKey = nameof(CanRevealMgmtKey);
    public const string CanAudit = nameof(CanAudit);

    public static readonly IReadOnlyDictionary<string, Role[]> Roles = new Dictionary<string, Role[]>
    {
        [CanIssue] = [Role.Admin, Role.SecurityOfficer],
        [CanList] = [Role.Admin, Role.SecurityOfficer, Role.Helpdesk],
        [CanViewDetails] = [Role.Admin, Role.SecurityOfficer],
        [CanRevealPuk] = [Role.Admin, Role.SecurityOfficer, Role.Helpdesk],
        [CanRevealMgmtKey] = [Role.Admin],
        [CanAudit] = [Role.Admin],
    };
}
