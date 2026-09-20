using System.Globalization;
using System.Management.Automation;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.PowerShell;

/// <summary>
/// Signs in to a BlinkyLite server and keeps the token for this session.
/// </summary>
/// <example>
///   <code>Connect-BlinkyLite -Server https://blinkylite.corp.example:8443</code>
/// </example>
[Cmdlet(VerbsCommunications.Connect, "BlinkyLite")]
[OutputType(typeof(CurrentUser))]
public sealed class ConnectBlinkyLiteCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public Uri Server { get; set; } = null!;

    /// <summary>The operator's AD credentials; asked for when not given.</summary>
    [Parameter]
    [Credential]
    public PSCredential Credential { get; set; } = PSCredential.Empty;

    /// <summary>Language of the messages; defaults to the shell's.</summary>
    [Parameter]
    [ValidateSet("en", "de", "sv", "pl")]
    public string? Language { get; set; }

    protected override void ProcessRecord()
    {
        if (Language is { } language)
        {
            Strings.Current.Culture = CultureInfo.GetCultureInfo(language);
        }

        var credential = Credential == PSCredential.Empty || Credential is null
            ? Host.UI.PromptForCredential("BlinkyLite", Server.Host, Environment.UserName, Environment.UserDomainName)
            : Credential;

        if (credential is null)
        {
            return;
        }

        var client = new ServerClient(Server);

        try
        {
            // The password is turned into a string here, at the one call that
            // needs it, and nowhere earlier.
            var user = client.LoginAsync(
                    credential.UserName,
                    credential.GetNetworkCredential().Password)
                .GetAwaiter().GetResult();

            Session.Open(client, user, Server);
            WriteObject(user);
        }
        catch (Exception e)
        {
            client.Dispose();
            ThrowTerminatingError(Problems.Of(e));
        }
    }
}

/// <summary>Forgets the token. The session ending does the same thing.</summary>
[Cmdlet(VerbsCommunications.Disconnect, "BlinkyLite")]
public sealed class DisconnectBlinkyLiteCommand : PSCmdlet
{
    protected override void ProcessRecord() => Session.Close();
}

/// <summary>What is in the reader, read-only.</summary>
[Cmdlet(VerbsCommon.Get, "BlinkyLiteCard")]
[OutputType(typeof(PSObject))]
public sealed class GetBlinkyLiteCardCommand : PSCmdlet
{
    /// <summary>Part of a reader's name, when the machine has several.</summary>
    [Parameter]
    public string? Reader { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            using var card = CardAccess.Open(Reader);
            var inventory = card.Session.ReadInventory(includeCertificates: false);

            var result = new PSObject();
            result.Properties.Add(new PSNoteProperty("Reader", card.Reader));
            result.Properties.Add(new PSNoteProperty("Serial", inventory.SerialNumber));
            result.Properties.Add(new PSNoteProperty("Firmware", inventory.Firmware.ToString()));
            result.Properties.Add(new PSNoteProperty("ManagementKey", inventory.ManagementKey?.Algorithm));
            result.Properties.Add(new PSNoteProperty("ManagementKeyIsFactory", inventory.ManagementKey?.IsDefault));
            result.Properties.Add(new PSNoteProperty("Pin", inventory.Pin.State));
            result.Properties.Add(new PSNoteProperty("Puk", inventory.Puk.State));
            result.Properties.Add(new PSNoteProperty("Slot9AEmpty",
                inventory.Slots.First(s => s.Slot == PivSlotName).IsEmpty));

            WriteObject(result);
        }
        catch (NoCardException e)
        {
            WriteError(Problems.Of(e));
        }
    }

    private static Piv.PivSlot PivSlotName => Piv.PivSlot.Authentication;
}

/// <summary>Finds the person a key is for, through the server.</summary>
[Cmdlet(VerbsCommon.Find, "BlinkyLiteUser")]
[OutputType(typeof(DirectoryUser))]
public sealed class FindBlinkyLiteUserCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Query { get; set; } = "";

    protected override void ProcessRecord()
    {
        var client = Session.Require(this);

        try
        {
            foreach (var user in client.SearchAsync(Query).GetAwaiter().GetResult())
            {
                WriteObject(user);
            }
        }
        catch (Exception e)
        {
            WriteError(Problems.Of(e));
        }
    }
}

/// <summary>The profiles this server allows, as the administrator set them.</summary>
[Cmdlet(VerbsCommon.Get, "BlinkyLiteProfile")]
[OutputType(typeof(IssuanceProfile))]
public sealed class GetBlinkyLiteProfileCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        var client = Session.Require(this);

        try
        {
            foreach (var profile in client.ProfilesAsync().GetAwaiter().GetResult())
            {
                WriteObject(profile);
            }
        }
        catch (Exception e)
        {
            WriteError(Problems.Of(e));
        }
    }
}

/// <summary>
/// Issues a key: the whole thing, on the same engine as the WPF client.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>-Pin</c> parameter and there will not be one. The PIN is
/// typed by the person the key is for, into the host, and it is never a
/// parameter, a variable or a line in a transcript.
/// </para>
/// <para>
/// <c>-WhatIf</c> works and stops before anything is written: the reservation
/// on the server is the first irreversible step, because from then on a token
/// exists that somebody has to account for.
/// </para>
/// </remarks>
/// <example>
///   <code>New-BlinkyLiteIssuance -User CORP\jkowalski</code>
/// </example>
[Cmdlet(VerbsCommon.New, "BlinkyLiteIssuance", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(IssuanceOutcome))]
public sealed class NewBlinkyLiteIssuanceCommand : PSCmdlet
{
    /// <summary><c>DOMAIN\sAMAccountName</c>, a UPN, or anything the search finds exactly one of.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public string User { get; set; } = "";

    /// <summary>The profile by name; not needed when the server has one.</summary>
    [Parameter]
    public string? Profile { get; set; }

    [Parameter]
    public string? Reader { get; set; }

    /// <summary>The enrolment agent certificate, by thumbprint, when there are several.</summary>
    [Parameter]
    public string? AgentThumbprint { get; set; }

    protected override void ProcessRecord()
    {
        var client = Session.Require(this);

        try
        {
            var target = FindOne(client);
            var profile = ChooseProfile(client);
            var agent = ChooseAgent();

            if (target is null || profile is null || agent is null)
            {
                return;
            }

            if (!ShouldProcess(
                    $"{target.DisplayName} ({target.SamAccount}), profil {profile.Name}",
                    "Wydac klucz"))
            {
                return;
            }

            using var card = CardAccess.Open(Reader);

            var progress = new Progress<string>(key => WriteVerbose(Strings.Current[key]));
            var outcome = new IssuanceRunner(client).RunAsync(
                    card.Session, target, profile, agent,
                    new HostPinPrompt(Host), Environment.MachineName, progress)
                .GetAwaiter().GetResult();

            WriteObject(outcome);
        }
        catch (Exception e)
        {
            ThrowTerminatingError(Problems.Of(e));
        }
    }

    /// <summary>
    /// Exactly one person, or a refusal.
    /// </summary>
    /// <remarks>
    /// Never a guess. A script that issued to the first of several matches
    /// would put somebody else's certificate on the card in the operator's
    /// hand, and nothing downstream would notice.
    /// </remarks>
    private DirectoryUser? FindOne(ServerClient client)
    {
        var found = client.SearchAsync(User).GetAwaiter().GetResult();

        // An exact account name or UPN wins over a list: a person and their
        // own administrative account come back together because they share a
        // mailbox, and one of the two is named precisely.
        if (DirectoryMatch.Exact(User, found) is { } exact)
        {
            return exact;
        }

        switch (found.Count)
        {
            case 1:
                return found[0];

            case 0:
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Nikt nie pasuje do \"{User}\"."),
                    "BlinkyLite.NoSuchUser", ErrorCategory.ObjectNotFound, User));
                return null;

            default:
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Do \"{User}\" pasuje {found.Count} osob: "
                        + string.Join(", ", found.Select(u => u.SamAccount))
                        + ". Podaj dokladna nazwe konta albo UPN."),
                    "BlinkyLite.AmbiguousUser", ErrorCategory.InvalidArgument, User));
                return null;
        }
    }

    private IssuanceProfile? ChooseProfile(ServerClient client)
    {
        var profiles = client.ProfilesAsync().GetAwaiter().GetResult();

        if (Profile is { } wanted)
        {
            var named = profiles.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException(
                        $"{Strings.Current[ErrorCodes.ProfileUnknown]} ({wanted})"),
                    "BlinkyLite.NoSuchProfile", ErrorCategory.ObjectNotFound, wanted));
            }

            return named;
        }

        if (profiles.Count == 1)
        {
            return profiles[0];
        }

        WriteError(new ErrorRecord(
            new InvalidOperationException(
                "Serwer ma wiecej niz jeden profil: "
                + string.Join(", ", profiles.Select(p => p.Name))
                + ". Wskaz go parametrem -Profile."),
            "BlinkyLite.ProfileNeeded", ErrorCategory.InvalidArgument, null));

        return null;
    }

    private System.Security.Cryptography.X509Certificates.X509Certificate2? ChooseAgent()
    {
        var agents = EnrolmentAgent.Find();

        var chosen = AgentThumbprint is { } wanted
            ? agents.FirstOrDefault(a => a.Thumbprint.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : agents.FirstOrDefault(a => a.IsUsable);

        if (chosen is null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException(Strings.Current[ErrorCodes.AgentMissing]),
                "BlinkyLite.NoAgent", ErrorCategory.ObjectNotFound, null));
        }

        return chosen?.Certificate;
    }
}
