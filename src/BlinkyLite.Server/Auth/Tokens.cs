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

    /// <summary>
    /// The audience of the ticket between the password and the code. Another
    /// value than <see cref="Audience"/> is the whole protection: the access
    /// token's validation refuses a ticket, and the ticket's refuses a token.
    /// </summary>
    public const string SecondFactorAudience = "BlinkyLite/second-factor";

    /// <summary>Long enough to open an app and type six digits, or to scan a QR code the first time.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

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
        var expires = clock.GetUtcNow().AddMinutes(options.LifetimeMinutes);
        return new LoginResponse(Create(user, JwtOptions.Audience, expires), expires, user);
    }

    /// <summary>Proof of a correct password, good only for the second step (0027).</summary>
    public LoginChallenge IssueTicket(CurrentUser user, string next)
    {
        var expires = clock.GetUtcNow().Add(JwtOptions.TicketLifetime);
        return new LoginChallenge(next, Create(user, JwtOptions.SecondFactorAudience, expires), expires);
    }

    private string Create(CurrentUser user, string audience, DateTimeOffset expires)
    {
        var now = clock.GetUtcNow();

        var claims = new Dictionary<string, object>
        {
            [TokenClaims.Sid] = user.Sid,
            [TokenClaims.Upn] = user.Upn,
            [TokenClaims.Name] = user.DisplayName,
            [TokenClaims.Role] = user.Roles.Select(r => r.ToString()).ToArray(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = credentials,
        });
    }

    public static TokenValidationParameters ValidationParameters(JwtOptions options, string audience = JwtOptions.Audience) => new()
    {
        ValidIssuer = JwtOptions.Issuer,
        ValidAudience = audience,
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
    public const string CanResetSecondFactor = nameof(CanResetSecondFactor);

    /// <summary>
    /// The second sign-in step. Authenticated by the ticket scheme alone, so an
    /// access token does not reach it and a ticket reaches nothing else.
    /// </summary>
    public const string SecondFactor = nameof(SecondFactor);

    /// <summary>The authentication scheme behind <see cref="SecondFactor"/>.</summary>
    public const string TicketScheme = "Ticket";

    /// <summary>
    /// The Windows sign-in (0025): a Kerberos ticket and nothing else. No role
    /// is required, because none is known yet - the roles come from AD after
    /// the ticket, exactly as after a password.
    /// </summary>
    public const string WindowsIdentity = nameof(WindowsIdentity);

    public static readonly IReadOnlyDictionary<string, Role[]> Roles = new Dictionary<string, Role[]>
    {
        [CanIssue] = [Role.Admin, Role.SecurityOfficer],
        [CanList] = [Role.Admin, Role.SecurityOfficer, Role.Helpdesk],
        [CanViewDetails] = [Role.Admin, Role.SecurityOfficer],
        [CanRevealPuk] = [Role.Admin, Role.SecurityOfficer, Role.Helpdesk],
        [CanRevealMgmtKey] = [Role.Admin],
        [CanAudit] = [Role.Admin],
        [CanResetSecondFactor] = [Role.Admin],
        [SecondFactor] = [Role.Admin, Role.SecurityOfficer, Role.Helpdesk],
        [WindowsIdentity] = [],
    };
}
