using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace BlinkyLite.UnitTests;

public sealed class SidTests
{
    [Fact]
    public void A_binary_SID_reads_back_as_text()
    {
        byte[] sid =
        [
            1, 5, 0, 0, 0, 0, 0, 5,                                     // revision, 5 sub-authorities, authority 5
            21, 0, 0, 0,                                                // 21
            100, 0, 0, 0, 200, 0, 0, 0, 44, 1, 0, 0, 77, 4, 0, 0,       // 100, 200, 300, 1101
        ];

        Assert.Equal("S-1-5-21-100-200-300-1101", Sid.FromBinary(sid));
    }

    [Fact]
    public void A_well_known_SID_comes_out_right()
    {
        byte[] everyone = [1, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0];

        Assert.Equal("S-1-1-0", Sid.FromBinary(everyone));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0 })]
    public void Anything_that_is_not_a_SID_is_refused(byte[] bytes) =>
        Assert.Throws<ArgumentException>(() => Sid.FromBinary(bytes));
}

public sealed class LdapFilterTests
{
    [Theory]
    [InlineData("kowalski", "kowalski")]
    [InlineData("*", @"\2a")]
    [InlineData(")(objectClass=*", @"\29\28objectClass=\2a")]
    [InlineData(@"CORP\jan", @"CORP\5cjan")]
    public void Typed_text_cannot_change_the_filter(string input, string escaped) =>
        Assert.Equal(escaped, LdapFilter.Escape(input));
}

public sealed class LoginNameTests
{
    [Theory]
    [InlineData("jkowalski", @"CORP\jkowalski", "(sAMAccountName=jkowalski)")]
    [InlineData(@"CORP\jkowalski", @"CORP\jkowalski", "(sAMAccountName=jkowalski)")]
    // Another domain still binds in ours: BlinkyLite serves one domain and must
    // not authenticate somebody across a trust by accident.
    [InlineData(@"OTHER\jkowalski", @"CORP\jkowalski", "(sAMAccountName=jkowalski)")]
    [InlineData("jkowalski@corp.example", "jkowalski@corp.example", "(userPrincipalName=jkowalski@corp.example)")]
    public void A_typed_name_becomes_a_bind_name_and_a_filter(string typed, string bind, string filter)
    {
        var name = LoginName.Parse(typed, "CORP");

        Assert.Equal(bind, name.BindName);
        Assert.Equal(filter, name.SearchFilter);
    }
}

public sealed class RoleMapTests
{
    [Fact]
    public void A_group_maps_to_its_role_and_to_nothing_else()
    {
        var map = Map(("Admin", "S-1-5-21-1-2-3-1100"), ("SecurityOfficer", "S-1-5-21-1-2-3-1101"));

        Assert.Equal([Role.SecurityOfficer], map.RolesFor(["S-1-5-21-1-2-3-1101", "S-1-5-21-1-2-3-9999"]));
        Assert.Empty(map.RolesFor(["S-1-5-21-1-2-3-9999"]));
    }

    [Fact]
    public void A_group_name_instead_of_a_SID_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Map(("Admin", @"CORP\BlinkyLite-Admins")));

        Assert.Contains("not a SID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_where_nobody_could_issue_is_refused()
    {
        // Admin and SecurityOfficer are the groups that hold Enrollment Agent
        // rights on the CA (D-17); leaving both out is a configuration mistake.
        var error = Assert.Throws<InvalidOperationException>(() => Map(("Helpdesk", "S-1-5-21-1-2-3-1102")));

        Assert.Contains("Enrollment Agent", error.Message, StringComparison.Ordinal);
    }

    private static RoleMap Map(params (string Role, string Sid)[] groups)
    {
        var values = groups.Select(g => new KeyValuePair<string, string?>($"Roles:{g.Role}:0", g.Sid));
        return RoleMap.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}

public sealed class TlsGuardTests
{
    [Theory]
    [InlineData("urls", "https://+:8443")]
    [InlineData("URLS", "http://localhost:5080;https://localhost:8443")]
    [InlineData("Kestrel:Endpoints:Https:Url", "https://*:8443")]
    [InlineData("HTTPS_PORTS", "8443")]
    public void An_HTTPS_endpoint_is_recognised(string key, string value) =>
        Assert.True(BlinkyLite.Server.Startup.ServerSetup.HasHttpsEndpoint(Configuration((key, value))));

    [Theory]
    [InlineData("urls", "http://+:8080")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://*:8080")]
    public void Plain_HTTP_alone_is_not(string key, string value) =>
        Assert.False(BlinkyLite.Server.Startup.ServerSetup.HasHttpsEndpoint(Configuration((key, value))));

    [Fact]
    public void No_endpoint_at_all_is_not_HTTPS() =>
        Assert.False(BlinkyLite.Server.Startup.ServerSetup.HasHttpsEndpoint(Configuration()));

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
