using BlinkyLite.Contracts;

namespace BlinkyLite.Issuance;

/// <summary>
/// Turning what somebody typed into exactly one person, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// A search is deliberately generous - it looks at the account name, the UPN,
/// the display name and the mail address - so a person and their own
/// administrative account come back together, because they share a mailbox.
/// That is right for a list somebody picks from and useless for a command that
/// has to act.
/// </para>
/// <para>
/// So: an exact answer wins over a partial one, and anything still ambiguous
/// is refused. Never the first of several. A certificate issued to the wrong
/// person sits on a card in somebody's hand and nothing downstream notices.
/// </para>
/// </remarks>
public static class DirectoryMatch
{
    /// <summary>
    /// The one person the query names exactly, or null when it names none or
    /// several.
    /// </summary>
    public static DirectoryUser? Exact(string query, IReadOnlyList<DirectoryUser> candidates)
    {
        var typed = query.Trim();
        var bare = Bare(typed);

        var exact = candidates
            .Where(user =>
                Same(user.SamAccount, typed)
                || Same(Bare(user.SamAccount), bare)
                || Same(user.Upn, typed)
                || Same(Before(user.Upn, '@'), bare))
            .ToList();

        return exact.Count == 1 ? exact[0] : null;
    }

    /// <summary>The account name without its domain: <c>CORP\jkowalski</c> becomes <c>jkowalski</c>.</summary>
    public static string Bare(string name)
    {
        var backslash = name.LastIndexOf('\\');

        return backslash >= 0 && backslash < name.Length - 1 ? name[(backslash + 1)..] : name;
    }

    private static string Before(string value, char separator)
    {
        var at = value.IndexOf(separator);

        return at > 0 ? value[..at] : value;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
