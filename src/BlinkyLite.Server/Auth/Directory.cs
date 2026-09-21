using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using BlinkyLite.Contracts;

namespace BlinkyLite.Server.Auth;

/// <summary>An account as AD describes it after a successful bind.</summary>
public sealed record DirectoryAccount(DirectoryUser User, IReadOnlyCollection<string> GroupSids);

/// <summary>The directory could not be asked at all - as opposed to "wrong password".</summary>
public sealed class DirectoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Active Directory, read-only. The server never writes to it.</summary>
public interface IDirectory
{
    /// <summary>Binds as the user. Null means the credentials were refused.</summary>
    Task<DirectoryAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Finds people who can receive a key, as the read-only service account.</summary>
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>
    /// The one account with this SID, or null when AD has none.
    /// </summary>
    /// <remarks>
    /// An issuance names its target by SID and by nothing else, so the server
    /// asks AD who that is instead of believing a UPN and a SID that arrived
    /// together in one request. A SID also outlives a rename, which a sAM
    /// account name does not.
    /// </remarks>
    Task<DirectoryUser?> FindBySidAsync(string sid, CancellationToken ct = default);

    /// <summary>
    /// The account behind a Kerberos principal and its groups, as the
    /// read-only service account (0025). Null when AD has no such account.
    /// </summary>
    /// <remarks>
    /// With a password the operator's own bind reads their groups; with
    /// Kerberos there is no bind as the operator, so the service account reads
    /// <c>tokenGroups</c> for them. Same attribute, same transitive groups - a
    /// role must not depend on how somebody signed in.
    /// </remarks>
    Task<DirectoryAccount?> FindForKerberosAsync(string samAccountName, CancellationToken ct = default);
}

/// <summary>Stands in when the Ldap section is missing, so the answer is 503 and not a 500.</summary>
public sealed class UnconfiguredDirectory : IDirectory
{
    private const string Message = "Ldap is not configured; BlinkyLite cannot sign anybody in.";

    public Task<DirectoryAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default) =>
        throw new DirectoryUnavailableException(Message);

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int limit, CancellationToken ct = default) =>
        throw new DirectoryUnavailableException(Message);

    public Task<DirectoryUser?> FindBySidAsync(string sid, CancellationToken ct = default) =>
        throw new DirectoryUnavailableException(Message);

    public Task<DirectoryAccount?> FindForKerberosAsync(string samAccountName, CancellationToken ct = default) =>
        throw new DirectoryUnavailableException(Message);
}

public sealed class LdapOptions
{
    public const string Section = "Ldap";

    /// <summary>A domain controller or the domain's DNS name.</summary>
    [Required]
    public string Server { get; set; } = "";

    public int Port { get; set; } = 636;

    /// <summary>
    /// Ldaps (port 636) or StartTls (port 389, upgraded). There is no plain
    /// option: the operator's password travels in the bind.
    /// </summary>
    public LdapTransport Transport { get; set; } = LdapTransport.Ldaps;

    [Required]
    public string BaseDn { get; set; } = "";

    /// <summary>NetBIOS name, for <c>DOMAIN\user</c> - the form the CA's RequesterName needs.</summary>
    [Required]
    public string NetBiosDomain { get; set; } = "";

    /// <summary>Read-only account used for searching users; never for signing anyone in.</summary>
    [Required]
    public string ServiceAccount { get; set; } = "";

    [Required]
    public string ServicePassword { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 10;
}

public enum LdapTransport
{
    Ldaps,
    StartTls,
}

/// <summary>
/// AD over System.DirectoryServices.Protocols, which works on Linux (libldap)
/// as well as Windows. Certificate trust comes from the operating system; on
/// Linux that means the CA in the system store or LDAPTLS_CACERT.
/// </summary>
public sealed class LdapDirectory(LdapOptions options) : IDirectory
{
    private const int UserAccountDisabled = 0x2;
    private const int InvalidCredentials = 49;

    private static readonly string[] UserAttributes =
        ["distinguishedName", "sAMAccountName", "userPrincipalName", "displayName", "objectSid", "userAccountControl"];

    public Task<DirectoryAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default) =>
        Task.Run(() => Authenticate(username, password), ct);

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int limit, CancellationToken ct = default) =>
        Task.Run(() => Search(query, limit), ct);

    public Task<DirectoryUser?> FindBySidAsync(string sid, CancellationToken ct = default) =>
        Task.Run(() => FindBySid(sid), ct);

    public Task<DirectoryAccount?> FindForKerberosAsync(string samAccountName, CancellationToken ct = default) =>
        Task.Run(() => FindForKerberos(samAccountName), ct);

    private DirectoryAccount? Authenticate(string username, string password)
    {
        // An LDAP simple bind with an empty password is an "unauthenticated
        // bind" (RFC 4513 5.1.2): many servers accept it as success. Checked
        // here as well as in the endpoint, because this is the place it bites.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var name = LoginName.Parse(username, options.NetBiosDomain);

        using var connection = Connect();
        try
        {
            connection.Bind(new NetworkCredential(name.BindName, password));
        }
        catch (LdapException e) when (e.ErrorCode == InvalidCredentials)
        {
            return null;
        }
        catch (LdapException e)
        {
            throw new DirectoryUnavailableException($"LDAP bind failed: {e.Message} ({e.ErrorCode})", e);
        }

        var entry = FindOne(connection, name.SearchFilter)
            ?? throw new DirectoryUnavailableException($"Bound as {name.BindName} but found no user object for it.");

        return new DirectoryAccount(ToUser(entry), TokenGroups(connection, entry));
    }

    private DirectoryAccount? FindForKerberos(string samAccountName)
    {
        // A sAMAccountName has no wildcard and no filter syntax in it; one
        // that does is not a name Kerberos gave us.
        if (string.IsNullOrWhiteSpace(samAccountName) || samAccountName.Length > 64
            || samAccountName.IndexOfAny(['*', '(', ')', '\\', '\0', '/', '@']) >= 0)
        {
            return null;
        }

        using var connection = Connect();
        try
        {
            connection.Bind(new NetworkCredential(options.ServiceAccount, options.ServicePassword));
        }
        catch (LdapException e)
        {
            throw new DirectoryUnavailableException($"Service account bind failed: {e.Message} ({e.ErrorCode})", e);
        }

        var entry = FindOne(connection,
            $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapFilter.Escape(samAccountName)}))");
        if (entry is null)
        {
            return null;
        }

        var groups = TokenGroups(connection, entry);

        // Every account is at least in Domain Users. None at all means the
        // service account may not read tokenGroups of other users - and then
        // everybody would come out without a role, which reads as "you have no
        // access" rather than as the configuration problem it is.
        if (groups.Count == 0)
        {
            throw new DirectoryUnavailableException(
                $"tokenGroups of {samAccountName} came back empty for {options.ServiceAccount}. " +
                "Add the service account to 'Windows Authorization Access Group' (docs/11).");
        }

        return new DirectoryAccount(ToUser(entry), groups);
    }

    private static List<string> TokenGroups(LdapConnection connection, SearchResultEntry entry)
    {
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(entry.DistinguishedName, "(objectClass=*)", SearchScope.Base, "tokenGroups"));

        return response.Entries.Cast<SearchResultEntry>()
            .SelectMany(e => Values(e, "tokenGroups"))
            .Select(Sid.FromBinary)
            .ToList();
    }

    private IReadOnlyList<DirectoryUser> Search(string query, int limit)
    {
        using var connection = Connect();
        try
        {
            connection.Bind(new NetworkCredential(options.ServiceAccount, options.ServicePassword));
        }
        catch (LdapException e)
        {
            throw new DirectoryUnavailableException($"Service account bind failed: {e.Message} ({e.ErrorCode})", e);
        }

        // DOMAIN\user is the form this server prints in every result and the
        // form an operator pastes back in; nobody has a backslash in a
        // sAMAccountName, so the search found nothing and said so politely.
        var typed = query.Trim();
        var backslash = typed.LastIndexOf('\\');
        if (backslash >= 0 && backslash < typed.Length - 1)
        {
            typed = typed[(backslash + 1)..];
        }

        var term = LdapFilter.Escape(typed);
        var filter = "(&(objectCategory=person)(objectClass=user)" +
                     $"(|(sAMAccountName={term}*)(userPrincipalName={term}*)(displayName=*{term}*)(mail={term}*)))";

        var request = new SearchRequest(options.BaseDn, filter, SearchScope.Subtree, UserAttributes) { SizeLimit = limit };
        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request);
        }
        catch (DirectoryOperationException e) when (e.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            response = (SearchResponse)e.Response;
        }

        return response.Entries.Cast<SearchResultEntry>().Select(ToUser)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads one account by SID, as the read-only service account.
    /// </summary>
    /// <remarks>
    /// The search base is the SID alias AD accepts in place of a DN, so the
    /// SID never has to be escaped into a filter as binary - that escaping is
    /// the part everyone gets wrong, and a wrong filter here would silently
    /// find nobody rather than fail.
    /// </remarks>
    private DirectoryUser? FindBySid(string sid)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-[0-9-]{1,120}$"))
        {
            return null;
        }

        using var connection = Connect();
        try
        {
            connection.Bind(new NetworkCredential(options.ServiceAccount, options.ServicePassword));
        }
        catch (LdapException e)
        {
            throw new DirectoryUnavailableException($"Service account bind failed: {e.Message} ({e.ErrorCode})", e);
        }

        var request = new SearchRequest(
            $"<SID={sid}>", "(objectClass=user)", SearchScope.Base, UserAttributes);

        try
        {
            var response = (SearchResponse)connection.SendRequest(request);
            return response.Entries.Cast<SearchResultEntry>().Select(ToUser).FirstOrDefault();
        }
        catch (DirectoryOperationException e) when (e.Response?.ResultCode is ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        try
        {
            if (options.Transport == LdapTransport.Ldaps)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }
        }
        catch (Exception e) when (e is LdapException or DirectoryOperationException)
        {
            connection.Dispose();
            throw new DirectoryUnavailableException($"TLS to {options.Server}:{options.Port} failed: {e.Message}", e);
        }

        return connection;
    }

    private SearchResultEntry? FindOne(LdapConnection connection, string filter)
    {
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(options.BaseDn, filter, SearchScope.Subtree, UserAttributes) { SizeLimit = 2 });

        return response.Entries.Count == 1 ? response.Entries[0] : null;
    }

    private DirectoryUser ToUser(SearchResultEntry entry)
    {
        var sam = Text(entry, "sAMAccountName") ?? "";
        var control = int.TryParse(Text(entry, "userAccountControl"), out var flags) ? flags : 0;

        return new DirectoryUser(
            SamAccount: $"{options.NetBiosDomain}\\{sam}",
            Upn: Text(entry, "userPrincipalName") ?? "",
            Sid: Sid.FromBinary(Values(entry, "objectSid").Single()),
            DisplayName: Text(entry, "displayName") ?? sam,
            Enabled: (control & UserAccountDisabled) == 0);
    }

    private static string? Text(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) ? entry.Attributes[attribute].GetValues(typeof(string)).Cast<string>().FirstOrDefault() : null;

    private static IEnumerable<byte[]> Values(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) ? entry.Attributes[attribute].GetValues(typeof(byte[])).Cast<byte[]>() : [];
}

/// <summary>How a typed username becomes an LDAP bind name and a search filter.</summary>
public sealed record LoginName(string BindName, string SearchFilter)
{
    public static LoginName Parse(string username, string netBiosDomain)
    {
        var name = username.Trim();

        if (name.Contains('@', StringComparison.Ordinal))
        {
            return new LoginName(name, $"(userPrincipalName={LdapFilter.Escape(name)})");
        }

        var slash = name.IndexOf('\\', StringComparison.Ordinal);
        var sam = slash >= 0 ? name[(slash + 1)..] : name;

        // The domain typed by the user is ignored for the search and replaced
        // for the bind: this server serves one domain, and binding as
        // OTHERDOMAIN\x through a trust is not something it should allow.
        return new LoginName($"{netBiosDomain}\\{sam}", $"(sAMAccountName={LdapFilter.Escape(sam)})");
    }
}

/// <summary>RFC 4515 escaping: user input in a filter must not be able to change the filter.</summary>
public static class LdapFilter
{
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}

/// <summary>
/// Binary SID to S-1-... text. SecurityIdentifier would do it, but only on
/// Windows, and the server also runs in a Linux container.
/// </summary>
public static class Sid
{
    public static string FromBinary(byte[] sid)
    {
        if (sid.Length < 8 || sid.Length != 8 + (sid[1] * 4))
        {
            throw new ArgumentException($"Not a SID: {sid.Length} bytes.", nameof(sid));
        }

        // The identifier authority is 48-bit big-endian; sub-authorities are
        // 32-bit little-endian. The mix is the format, not a bug.
        var authority = 0UL;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | sid[i];
        }

        var text = new StringBuilder($"S-{sid[0]}-{authority}");
        for (var i = 0; i < sid[1]; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan(8 + (i * 4), 4)));
        }

        return text.ToString();
    }
}
