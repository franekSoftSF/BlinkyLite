using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using Microsoft.Extensions.Configuration;

namespace BlinkyLite.UnitTests;

/// <summary>
/// 0025: a Kerberos ticket replaces the password, not the second factor, and
/// a server that cannot do Kerberos says so.
/// </summary>
public sealed class KerberosTests(ServerFactory server) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Without_a_keytab_the_answer_is_a_readable_503_and_not_a_challenge()
    {
        using var noKeytab = new ServerFactory { Keytab = null };

        var response = await noKeytab.CreateClient().PostAsync(KerberosGate.Path, null);

        await response.ShouldBe(HttpStatusCode.ServiceUnavailable, ErrorCodes.KerberosUnavailable);
        Assert.False(response.Headers.WwwAuthenticate.Any());
    }

    [Fact]
    public async Task Without_a_ticket_the_server_asks_for_Negotiate()
    {
        var response = await server.CreateClient().PostAsync(KerberosGate.Path, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
    }

    [Fact]
    public async Task A_ticket_yields_the_second_factor_ticket_and_never_a_token()
    {
        var sam = Account(ServerFactory.OfficerGroup);

        var response = await Negotiate($"{sam}@{ServerFactory.Realm}");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("token", body!.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(LoginNext.TotpSetup, body["next"].ToString());
    }

    [Fact]
    public async Task Kerberos_then_the_code_signs_in_with_the_roles_from_AD()
    {
        var sam = Account(ServerFactory.AdminGroup);
        using var client = server.CreateClient();

        var challenge = (await (await Negotiate($"{sam}@{ServerFactory.Realm}")).Content.ReadFromJsonAsync<LoginChallenge>())!;
        var secret = await ServerFactory.SetUp(client, challenge);
        var login = await ServerFactory.SecondStep(client, challenge, ServerFactory.TotpCode(secret));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var me = await client.GetFromJsonAsync<CurrentUser>("/api/auth/me");
        Assert.Equal([Role.Admin], me!.Roles);
    }

    [Fact]
    public async Task A_ticket_from_another_realm_is_refused_and_audited()
    {
        var sam = Account(ServerFactory.AdminGroup);
        var before = server.Procedures.Audits.Count;

        var response = await Negotiate($"{sam}@OTHER.EXAMPLE");

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal("kerberos-foreign-realm", server.Procedures.ReasonOf(before));
    }

    [Fact]
    public async Task A_principal_AD_does_not_know_is_told_so()
    {
        var response = await Negotiate($"nobody{Random.Shared.Next()}@{ServerFactory.Realm}");

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.KerberosNoAccount);
    }

    [Fact]
    public async Task A_proven_identity_without_a_role_is_still_no_role()
    {
        var sam = Account("S-1-5-21-100-200-300-9999");

        var response = await Negotiate($"{sam}@{ServerFactory.Realm}");

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.NoRole);
    }

    [Fact]
    public async Task A_service_principal_is_not_a_person_signing_in()
    {
        var response = await Negotiate($"HTTP/blinkylite.corp.example@{ServerFactory.Realm}");

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task A_disabled_account_is_refused_even_with_a_ticket()
    {
        var sam = $"off{Random.Shared.Next(1000, 9999)}";
        server.Directory.Accounts[sam] = new DirectoryAccount(
            new DirectoryUser($@"CORP\{sam}", $"{sam}@corp.example", $"S-1-5-21-100-200-300-{Random.Shared.Next(10_000, 99_999)}", sam, Enabled: false),
            [ServerFactory.AdminGroup]);

        var response = await Negotiate($"{sam}@{ServerFactory.Realm}");

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("jkowalski@CORP.EXAMPLE", "jkowalski", "CORP.EXAMPLE")]
    [InlineData("j.kowalski@corp.example", "j.kowalski", "corp.example")]
    [InlineData(@"CORP\jkowalski", null, null)]
    [InlineData("HTTP/host@CORP.EXAMPLE", null, null)]
    [InlineData("a@b@CORP.EXAMPLE", null, null)]
    [InlineData("@CORP.EXAMPLE", null, null)]
    [InlineData("jkowalski@", null, null)]
    public void A_Kerberos_name_is_an_account_and_a_realm_or_nothing(string name, string? account, string? realm)
    {
        var parsed = KerberosName.Parse(name);

        Assert.Equal(account, parsed?.Account);
        Assert.Equal(realm, parsed?.Realm);
    }

    [Fact]
    public void The_realm_defaults_to_the_domain_the_server_searches()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ldap:BaseDn"] = "DC=ems-ad, DC=emsdemolab,DC=pl" })
            .Build();

        var options = KerberosOptions.From(configuration);

        Assert.Equal("EMS-AD.EMSDEMOLAB.PL", options.Realm);
        Assert.True(options.AcceptsRealm("ems-ad.emsdemolab.pl"));
    }

    [Fact]
    public void An_empty_keytab_file_means_no_Kerberos()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.False(new KerberosOptions { KeytabPath = path }.IsAvailable);
            File.WriteAllBytes(path, [0x05, 0x02]);
            Assert.True(new KerberosOptions { KeytabPath = path }.IsAvailable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private string Account(string group)
    {
        var id = Random.Shared.Next(10_000, 99_999);
        var sam = $"krb{id}";
        server.Directory.Accounts[sam] = new DirectoryAccount(
            new DirectoryUser($@"CORP\{sam}", $"{sam}@corp.example", $"S-1-5-21-100-200-300-{id}", $"Krb {id}", true),
            [group]);
        return sam;
    }

    private Task<HttpResponseMessage> Negotiate(string principal)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, KerberosGate.Path);
        request.Headers.Add(FakeKerberosHandler.Header, principal);
        return server.CreateClient().SendAsync(request);
    }
}
