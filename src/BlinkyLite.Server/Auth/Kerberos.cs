using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;

namespace BlinkyLite.Server.Auth;

/// <summary>
/// Windows sign-in (0025): where the keytab is and which realm is ours.
/// </summary>
/// <remarks>
/// The keytab itself is read by GSSAPI, not by this code - through
/// <c>KRB5_KTNAME</c>, which the container sets to the Docker secret. This
/// class only answers "is there one", so that a server without it says so
/// instead of answering every Windows sign-in with a bare 401 (R-08).
/// </remarks>
public sealed class KerberosOptions
{
    public const string Section = "Kerberos";

    /// <summary>
    /// The realm tickets must come from, e.g. <c>DW-AD.DIGITALWORKSPACE.PL</c>.
    /// Empty: derived from <c>Ldap:BaseDn</c> - the same domain the server
    /// searches, which is the only one it can find people in.
    /// </summary>
    public string Realm { get; set; } = "";

    /// <summary>Empty: <c>KRB5_KTNAME</c>, which is also what GSSAPI reads.</summary>
    public string KeytabPath { get; set; } = "";

    public string EffectiveKeytabPath =>
        (string.IsNullOrWhiteSpace(KeytabPath) ? Environment.GetEnvironmentVariable("KRB5_KTNAME") ?? "" : KeytabPath)
            .Replace("FILE:", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A keytab that exists and is not empty. An empty file is how a
    /// deployment without Kerberos satisfies the Docker secret (docs/11).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            var path = EffectiveKeytabPath;
            try
            {
                return path.Length > 0 && File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool AcceptsRealm(string realm) =>
        Realm.Length > 0 && string.Equals(realm, Realm, StringComparison.OrdinalIgnoreCase);

    public static KerberosOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection(Section).Get<KerberosOptions>() ?? new KerberosOptions();

        if (string.IsNullOrWhiteSpace(options.Realm))
        {
            // DC=dw-ad,DC=digitalworkspace,DC=pl -> DW-AD.DIGITALWORKSPACE.PL
            var baseDn = configuration[$"{LdapOptions.Section}:BaseDn"] ?? "";
            options.Realm = string.Join('.', baseDn.Split(',')
                    .Select(part => part.Trim())
                    .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    .Select(part => part[3..]))
                .ToUpperInvariant();
        }

        return options;
    }
}

/// <summary>A Kerberos client name split into account and realm.</summary>
public sealed record KerberosName(string Account, string Realm)
{
    /// <summary>
    /// <c>user@REALM</c>, as GSSAPI on Linux names it; <c>DOMAIN\user</c> is
    /// refused, because a NetBIOS name is not a realm and guessing one from it
    /// is how a server ends up trusting the wrong domain.
    /// </summary>
    public static KerberosName? Parse(string name)
    {
        var at = name.LastIndexOf('@');
        if (at <= 0 || at == name.Length - 1)
        {
            return null;
        }

        var account = name[..at];

        // A service principal (HTTP/host) or an enterprise name with a second
        // '@' is not a person signing in.
        return account.Contains('/', StringComparison.Ordinal) || account.Contains('@', StringComparison.Ordinal)
            ? null
            : new KerberosName(account, name[(at + 1)..]);
    }
}

public static class KerberosGate
{
    public const string Path = "/api/auth/negotiate";

    /// <summary>
    /// Answers the Windows sign-in endpoint with a readable 503 when there is
    /// no keytab - before the Negotiate handler gets to send a challenge that
    /// no client could ever meet.
    /// </summary>
    public static IApplicationBuilder UseBlinkyLiteKerberosGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase)
                && !context.RequestServices.GetRequiredService<KerberosOptions>().IsAvailable)
            {
                await Problems.Of(StatusCodes.Status503ServiceUnavailable, ErrorCodes.KerberosUnavailable)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });
}
