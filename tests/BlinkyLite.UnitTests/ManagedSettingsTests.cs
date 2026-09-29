using System.Text.Json;
using BlinkyLite.Client;
using BlinkyLite.Ui.Configuration;
using BlinkyLite.Unlock;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The address from Group Policy (0057). The registry itself is not touched
/// here - a test that wrote to <c>SOFTWARE\Policies</c> would configure the
/// machine it runs on - so what is checked is everything around it: where the
/// value is read from, and that the "this came from a policy" flag can never
/// end up in a settings file.
/// </summary>
public sealed class ManagedSettingsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_key_is_the_one_the_deployment_instructions_name()
    {
        // Under Policies, so that a machine taken out of the policy's scope
        // stops being configured instead of keeping yesterday's address.
        Assert.Equal(@"SOFTWARE\Policies\BlinkyLite", ManagedSettings.Key);
        Assert.Equal("Server", ManagedSettings.ServerValue);

        var instructions = File.ReadAllText(
            Path.Combine(RepositoryConventionTests.Root.FullName, "docs", "11-wymagania-i-wdrozenie.md"));

        Assert.Contains(ManagedSettings.Key, instructions, StringComparison.Ordinal);
        Assert.Contains(ManagedSettings.ServerValue, instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag written to the file would outlive the policy and lock the box on
    /// a machine nobody configures any more.
    /// </summary>
    [Fact]
    public void The_unblock_tool_never_writes_the_policy_flag_to_its_file()
    {
        var json = JsonSerializer.Serialize(
            new UnlockSettings("https://blinkylite.example", FromPolicy: true), Json);

        Assert.DoesNotContain("fromPolicy", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(JsonSerializer.Deserialize<UnlockSettings>(
            """{"server":"https://blinkylite.example","fromPolicy":true}""", Json)!.FromPolicy);
    }

    [Fact]
    public void The_client_never_writes_the_policy_flag_to_its_file()
    {
        var json = JsonSerializer.Serialize(new ClientSettings("https://blinkylite.example"), Json);

        Assert.DoesNotContain("fromPolicy", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(JsonSerializer.Deserialize<ClientSettings>(
            """{"server":"https://blinkylite.example","serverFromPolicy":true}""", Json)!.ServerFromPolicy);
    }

    /// <summary>
    /// Nothing is remembered when the address is not the person's: the policy
    /// says it again at every start, and a copy in the profile would be the
    /// thing that disagrees with it later.
    /// </summary>
    [Fact]
    public void An_address_from_policy_is_not_written_to_the_profile()
    {
        var path = UnlockSettings.UserPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;

        new UnlockSettings("https://not-written.example", FromPolicy: true).Save();

        var after = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Assert.Equal(before, after);
    }
}
