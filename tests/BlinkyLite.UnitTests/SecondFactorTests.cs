using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;

namespace BlinkyLite.UnitTests;

/// <summary>
/// 0027: no token without a valid second factor, and no code twice.
/// </summary>
public sealed class SecondFactorTests(ServerFactory server) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task A_correct_password_yields_a_ticket_and_never_a_token()
    {
        var username = Operator();
        using var client = server.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "right"));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("token", body!.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(LoginNext.TotpSetup, body["next"].ToString());
    }

    [Fact]
    public async Task The_ticket_opens_nothing_but_the_second_step()
    {
        var username = Operator();
        using var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.Ticket);

        await (await client.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
        await (await client.GetAsync("/api/profiles")).ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task An_access_token_is_not_a_ticket()
    {
        using var signedIn = await server.SignedInAs(Role.Admin);

        var response = await signedIn.PostAsJsonAsync("/api/auth/totp", new SecondFactorRequest("123456"));

        await response.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Setup_ends_with_a_token_and_ten_backup_codes_shown_once()
    {
        var username = Operator();
        using var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);
        var secret = await ServerFactory.SetUp(client, challenge);

        var login = await ServerFactory.SecondStep(client, challenge, ServerFactory.TotpCode(secret));

        Assert.False(string.IsNullOrEmpty(login.Token));
        Assert.Equal(BackupCodes.Count, login.BackupCodes!.Count);

        // The next sign-in asks for a code and hands out no codes.
        var next = await ServerFactory.Challenge(client, username);
        Assert.Equal(LoginNext.Totp, next.Next);
        var again = await ServerFactory.SecondStep(client, next, ServerFactory.TotpCode(secret, stepOffset: 1));
        Assert.Null(again.BackupCodes);
    }

    [Fact]
    public async Task A_wrong_code_is_401_and_audited_as_a_denied_sign_in()
    {
        var username = Operator();
        using var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);
        var secret = await ServerFactory.SetUp(client, challenge);
        var before = server.Procedures.Audits.Count;

        var wrong = ServerFactory.TotpCode(secret) == "000000" ? "111111" : "000000";
        var response = await ServerFactory.SendCode(client, challenge, wrong);

        await response.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        Assert.Equal(AuditAction.AuthDenied, server.Procedures.Audits[before].Action);
        Assert.Equal("totp-invalid", server.Procedures.ReasonOf(before));
    }

    [Fact]
    public async Task A_code_that_signed_in_once_does_not_sign_in_again()
    {
        var (client, username, secret, _) = await Enrolled();
        var code = ServerFactory.TotpCode(secret, stepOffset: 1);

        await ServerFactory.SecondStep(client, await ServerFactory.Challenge(client, username), code);
        var replay = await ServerFactory.SendCode(client, await ServerFactory.Challenge(client, username), code);

        await replay.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        Assert.Equal("totp-replayed", server.Procedures.ReasonOf(server.Procedures.Audits.Count - 1));
        client.Dispose();
    }

    [Fact]
    public async Task An_older_code_than_the_last_one_used_is_refused_too()
    {
        var (client, username, secret, _) = await Enrolled();

        await ServerFactory.SecondStep(client, await ServerFactory.Challenge(client, username), ServerFactory.TotpCode(secret, 1));
        var older = await ServerFactory.SendCode(client, await ServerFactory.Challenge(client, username), ServerFactory.TotpCode(secret, -1));

        await older.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        client.Dispose();
    }

    [Fact]
    public async Task A_backup_code_signs_in_once_and_says_how_many_are_left()
    {
        var (client, username, _, codes) = await Enrolled();

        var first = await ServerFactory.SecondStep(client, await ServerFactory.Challenge(client, username), codes[3].ToLowerInvariant());
        var second = await ServerFactory.SendCode(client, await ServerFactory.Challenge(client, username), codes[3]);

        Assert.Equal(BackupCodes.Count - 1, first.BackupCodesLeft);
        await second.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        client.Dispose();
    }

    [Fact]
    public async Task A_backup_code_does_not_confirm_a_setup()
    {
        var username = Operator();
        using var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);
        await ServerFactory.SetUp(client, challenge);

        var response = await ServerFactory.SendCode(client, challenge, "ABCDE-FGHJK");

        await response.ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
    }

    [Fact]
    public async Task A_confirmed_second_factor_cannot_be_replaced_by_whoever_has_the_password()
    {
        var (client, username, _, _) = await Enrolled();

        var response = await client.SendAsync(ServerFactory.WithTicket(
            HttpMethod.Post, "/api/auth/totp/setup", await ServerFactory.Challenge(client, username)));

        await response.ShouldBe(HttpStatusCode.Conflict, ErrorCodes.TotpAlreadyConfigured);
        client.Dispose();
    }

    [Fact]
    public async Task A_code_before_any_setup_is_told_to_set_up()
    {
        var username = Operator();
        using var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);

        var response = await ServerFactory.SendCode(client, challenge, "123456");

        await response.ShouldBe(HttpStatusCode.Conflict, ErrorCodes.TotpSetupRequired);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_even_for_the_right_one()
    {
        var (client, username, secret, _) = await Enrolled();
        var challenge = await ServerFactory.Challenge(client, username);
        var right = ServerFactory.TotpCode(secret, 1);
        var wrong = right == "000000" ? "111111" : "000000";

        for (var attempt = 0; attempt < FailedLogins.Limit; attempt++)
        {
            await (await ServerFactory.SendCode(client, challenge, wrong)).ShouldBe(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        }

        await (await ServerFactory.SendCode(client, challenge, right)).ShouldBe(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        client.Dispose();
    }

    [Fact]
    public async Task Only_an_Admin_can_reset_somebody_elses_second_factor()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);
        await (await officer.PostAsJsonAsync("/api/operators/S-1-5-21-1-2-3-77/totp/reset", new TotpResetRequest("lost phone")))
            .ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        using var admin = await server.SignedInAs(Role.Admin);
        var response = await admin.PostAsJsonAsync("/api/operators/S-1-5-21-1-2-3-77/totp/reset", new TotpResetRequest("lost phone"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(server.Procedures.Resets, r => r.OperatorSid == "S-1-5-21-1-2-3-77" && r.Reason == "lost phone");
    }

    /// <summary>An operator of their own, so that tests sharing the server do not share a second factor.</summary>
    private string Operator()
    {
        var id = Random.Shared.Next(10_000, 99_999);
        var username = $"op{id}";
        server.Directory.Accounts[username] = new DirectoryAccount(
            new DirectoryUser($@"CORP\{username}", $"{username}@corp.example", $"S-1-5-21-100-200-300-{id}", $"Op {id}", true),
            [ServerFactory.OfficerGroup]);
        return username;
    }

    private async Task<(HttpClient Client, string Username, byte[] Secret, IReadOnlyList<string> BackupCodes)> Enrolled()
    {
        var username = Operator();
        var client = server.CreateClient();
        var challenge = await ServerFactory.Challenge(client, username);
        var secret = await ServerFactory.SetUp(client, challenge);
        var login = await ServerFactory.SecondStep(client, challenge, ServerFactory.TotpCode(secret, -1));
        return (client, username, secret, login.BackupCodes!);
    }
}
