using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;

namespace BlinkyLite.UnitTests;

public sealed class AuthEndpointTests(ServerFactory server) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task A_wrong_password_is_401_and_an_audit_event_without_a_SID()
    {
        server.Directory.Accounts["jkowalski"] = Account(ServerFactory.OfficerGroup);
        var before = server.Procedures.Audits.Count;

        var response = await server.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("jkowalski", "wrong"));

        await response.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var (action, _, actor) = server.Procedures.Audits[before];
        Assert.Equal(AuditAction.AuthDenied, action);
        Assert.Null(actor.Sid);
        Assert.Equal("invalid-credentials", server.Procedures.ReasonOf(before));
    }

    [Fact]
    public async Task A_correct_password_without_a_mapped_group_is_403_and_audited_with_the_SID()
    {
        server.Directory.Accounts["stranger"] = Account("S-1-5-21-100-200-300-9999");
        var before = server.Procedures.Audits.Count;

        var response = await server.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("stranger", "right"));

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.NoRole);
        Assert.Equal("no-role", server.Procedures.ReasonOf(before));
        Assert.NotNull(server.Procedures.Audits[before].Actor.Sid);
    }

    [Fact]
    public async Task An_empty_password_never_reaches_the_directory()
    {
        // An LDAP simple bind with an empty password can succeed as an
        // unauthenticated bind; the request must not get that far.
        server.Directory.Accounts["jkowalski"] = Account(ServerFactory.OfficerGroup);
        var before = server.Procedures.Audits.Count;

        var response = await server.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("jkowalski", ""));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        Assert.Equal(before, server.Procedures.Audits.Count);
    }

    [Fact]
    public async Task A_signed_in_operator_gets_a_token_that_names_the_roles_and_works_on_me()
    {
        using var client = await server.SignedInAs(Role.SecurityOfficer, Role.Helpdesk);

        var me = await client.GetFromJsonAsync<CurrentUser>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal("operator@corp.example", me.Upn);
        Assert.Equal("S-1-5-21-100-200-300-5000", me.Sid);
        Assert.Equal([Role.SecurityOfficer, Role.Helpdesk], me.Roles.Order());
        Assert.Equal(AuditAction.AuthLogin, server.Procedures.Audits[^1].Action);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401_with_a_message_key()
    {
        var response = await server.CreateClient().GetAsync("/api/auth/me");

        await response.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var other = new TokenService(new JwtOptions { SigningKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }, TimeProvider.System);
        var forged = other.Issue(new CurrentUser("x@corp.example", "S-1-5-21-1-2-3-4", "X", [Role.Admin]));

        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged.Token);

        await (await client.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var past = new FakeClock(DateTimeOffset.UtcNow.AddHours(-2));
        var expired = new TokenService(new JwtOptions { SigningKey = Convert.ToBase64String(new byte[32]) }, past)
            .Issue(new CurrentUser("x@corp.example", "S-1-5-21-1-2-3-4", "X", [Role.Admin]));

        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired.Token);

        await (await client.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_out_before_AD_does()
    {
        const string username = "lockme";
        server.Directory.Accounts[username] = Account(ServerFactory.OfficerGroup);
        using var client = server.CreateClient();

        for (var attempt = 0; attempt < FailedLogins.Limit; attempt++)
        {
            await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "wrong")))
                .ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        }

        // Even the right password now, so that BlinkyLite cannot be used to
        // exhaust somebody's AD lockout counter.
        var afterLockout = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($@"CORP\{username}", "right"));

        await afterLockout.ShouldBe(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal("locked-out", server.Procedures.ReasonOf(server.Procedures.Audits.Count - 1));
    }

    [Fact]
    public async Task A_directory_that_cannot_be_reached_is_503_not_401()
    {
        var factory = new ServerFactory();
        factory.Directory.Throws = new DirectoryUnavailableException("no route to the domain controller");

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("x", "y"));

        await response.ShouldBe(HttpStatusCode.ServiceUnavailable, ErrorCodes.DirectoryUnavailable);
        factory.Dispose();
    }

    [Fact]
    public async Task Only_roles_that_may_issue_can_search_the_directory()
    {
        server.Directory.Users.Add(new DirectoryUser(@"CORP\jkowalski", "jkowalski@corp.example", "S-1-5-21-1-2-3-42", "Jan Kowalski", true));

        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        using var officer = await server.SignedInAs(Role.SecurityOfficer);

        await (await helpdesk.GetAsync("/api/directory/users?q=Kowal")).ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var found = await officer.GetFromJsonAsync<List<DirectoryUser>>("/api/directory/users?q=Kowal");
        Assert.Equal(@"CORP\jkowalski", found!.Single().SamAccount);
    }

    [Fact]
    public async Task A_one_letter_search_is_refused_with_its_own_key()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);

        await (await officer.GetAsync("/api/directory/users?q=a")).ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.QueryTooShort);
    }

    [Fact]
    public async Task Health_needs_no_token()
    {
        var health = await server.CreateClient().GetFromJsonAsync<HealthResponse>("/health");

        Assert.Equal("ok", health!.Status);
    }

    private static DirectoryAccount Account(params string[] groups) => new(
        new DirectoryUser(@"CORP\jkowalski", "jkowalski@corp.example", "S-1-5-21-100-200-300-4242", "Jan Kowalski", true),
        groups);
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
