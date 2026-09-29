using System.IO;
using System.Security;
using Microsoft.Win32;

namespace BlinkyLite.Ui.Configuration;

/// <param name="Value">What the administrator set.</param>
/// <param name="Scope">Which hive it came from, for the log line the caller writes.</param>
public sealed record ManagedSetting(string Value, string Scope);

/// <summary>
/// What an administrator decided for this machine, through Group Policy.
/// </summary>
/// <remarks>
/// <para>
/// One value so far: the server's address. It exists because of the unblock
/// tool (0057) - somebody whose PIN is blocked is not the person who should be
/// asked to know the address of anything - but the client reads the same key,
/// because two applications reading two different keys for the same address is
/// how a fleet ends up half configured.
/// </para>
/// <para>
/// <c>Policies</c>, not the product's own key, is the part of the registry
/// Group Policy owns: values under it disappear when the policy stops
/// applying, so a machine taken out of scope stops being configured instead of
/// keeping yesterday's address for ever.
/// </para>
/// <para>
/// A policy wins over what the person typed. That is the whole meaning of the
/// word, and it is also the safer way round: the address decides where a
/// request to unblock a key is sent.
/// </para>
/// <para>
/// Nothing is logged here, because this library has no logger and does not
/// need one - the two callers have Serilog and write the line themselves.
/// </para>
/// </remarks>
public static class ManagedSettings
{
    /// <summary>Under both hives, because a policy can be aimed at a machine or at a user.</summary>
    public const string Key = @"SOFTWARE\Policies\BlinkyLite";

    public const string ServerValue = "Server";

    /// <summary>The address from policy, or null when nobody set one.</summary>
    public static ManagedSetting? Server =>
        Read(ServerValue) is { } found && Uri.TryCreate(found.Value, UriKind.Absolute, out var uri)
            ? found with { Value = uri.ToString().TrimEnd('/') }
            : null;

    /// <summary>
    /// The machine's value first: a user policy is the exception, and an
    /// administrator who set both meant the machine to win.
    /// </summary>
    private static ManagedSetting? Read(string name)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(Key);
                if (key?.GetValue(name) as string is { Length: > 0 } value)
                {
                    return new ManagedSetting(value.Trim(), hive.Name);
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                // A registry nobody may read is a machine without a policy, not
                // a reason to refuse to start.
            }
        }

        return null;
    }
}
