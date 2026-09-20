using System.ComponentModel.DataAnnotations;
using BlinkyLite.Contracts;

namespace BlinkyLite.Server.Issuing;

/// <summary>One profile as the administrator wrote it in <c>appsettings.json</c>.</summary>
public sealed class ProfileOptions
{
    /// <summary>What the operator sees, and what the station sends back.</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>The ADCS template NAME, never the display name - the CA compares the name.</summary>
    [Required]
    public string Template { get; set; } = "";

    /// <summary>Overrides <see cref="IssuanceOptions.CertificationAuthority"/> for this profile.</summary>
    public string? CaConfig { get; set; }

    public string KeyAlgorithm { get; set; } = "Rsa2048";

    public string PinPolicy { get; set; } = "Once";

    public string TouchPolicy { get; set; } = "Never";
}

/// <summary>
/// The <c>Issuance</c> section: what this server is allowed to issue.
/// </summary>
/// <remarks>
/// Configured centrally by an administrator and handed out by the server
/// (D-21). A station keeps no list of its own, so changing a template is one
/// change in one place rather than a walk around the desks.
/// </remarks>
public sealed class IssuanceOptions
{
    public const string Section = "Issuance";

    /// <summary><c>HOST\CA CN</c>, exactly as certutil prints it.</summary>
    public string CertificationAuthority { get; set; } = "";

    public List<ProfileOptions> Profiles { get; set; } = [];

    /// <summary>
    /// The profiles as the API hands them out, with the CA filled in.
    /// </summary>
    public IReadOnlyList<IssuanceProfile> Published() =>
    [
        .. Profiles.Select(p => new IssuanceProfile(
            p.Name,
            p.Template,
            p.CaConfig ?? CertificationAuthority,
            p.KeyAlgorithm,
            p.PinPolicy,
            p.TouchPolicy)),
    ];

    /// <summary>The profile by name, or null - a name from a request is never trusted to exist.</summary>
    public IssuanceProfile? Find(string? name) =>
        name is null ? null : Published().FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>
    /// What is wrong with this section, checked at startup rather than at the
    /// first issuance.
    /// </summary>
    /// <remarks>
    /// A server that starts with a broken profile list looks healthy until
    /// somebody stands at a desk with a token in their hand. Two profiles of
    /// the same name are in here because <see cref="Find"/> would silently pick
    /// the first, and the record would then name a template nobody chose.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Profiles.Count == 0)
        {
            problems.Add("Issuance:Profiles is empty; there is nothing this server could issue.");
            return problems;
        }

        foreach (var profile in Profiles)
        {
            var where = string.IsNullOrWhiteSpace(profile.Name) ? "a profile with no name" : $"profile \"{profile.Name}\"";

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                problems.Add("Every profile needs a Name; it is what the operator picks.");
            }

            if (string.IsNullOrWhiteSpace(profile.Template))
            {
                problems.Add($"{where} has no Template.");
            }

            if (string.IsNullOrWhiteSpace(profile.CaConfig ?? CertificationAuthority))
            {
                problems.Add($"{where} has no CA: set Issuance:CertificationAuthority or the profile's CaConfig.");
            }
        }

        var duplicates = Profiles
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
            .Select(g => g.Key);

        problems.AddRange(duplicates.Select(name =>
            $"Two profiles are called \"{name}\"; a request naming it would silently get the first."));

        return problems;
    }
}
