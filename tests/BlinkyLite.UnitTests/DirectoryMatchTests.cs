using BlinkyLite.Contracts;
using BlinkyLite.Issuance;

namespace BlinkyLite.UnitTests;

/// <summary>
/// Turning what an operator typed into one person - or into a refusal.
/// </summary>
/// <remarks>
/// From a real run: <c>-User jan.kowalski</c> matched the person and
/// their own administrative account, because the two share a mailbox, and the
/// cmdlet refused although one of them was named exactly.
/// </remarks>
public sealed class DirectoryMatchTests
{
    private static readonly DirectoryUser Person =
        new(@"DW-AD\jan.kowalski", "jan.kowalski@digitalworkspace.pl", "S-1-5-21-1-2-3-1106", "Jan Kowalski", true);

    private static readonly DirectoryUser Admin =
        new(@"DW-AD\adm_j.kowalski", "adm_j.kowalski@digitalworkspace.pl", "S-1-5-21-1-2-3-1107", "Admin Jan Kowalski", true);

    [Theory]
    [InlineData("jan.kowalski")]
    [InlineData(@"DW-AD\jan.kowalski")]
    [InlineData(@"dw-ad\JAN.KOWALSKI")]
    [InlineData("jan.kowalski@digitalworkspace.pl")]
    public void An_exact_name_wins_over_a_crowded_search(string typed)
    {
        var chosen = DirectoryMatch.Exact(typed, [Admin, Person]);

        Assert.Equal(Person.Sid, chosen?.Sid);
    }

    [Fact]
    public void A_partial_name_that_matches_two_people_chooses_neither()
    {
        // The refusal is the point. Issuing to the first of several puts
        // somebody else's certificate on the card in your hand.
        Assert.Null(DirectoryMatch.Exact("kowalski", [Admin, Person]));
    }

    [Fact]
    public void A_name_nobody_has_chooses_nobody() =>
        Assert.Null(DirectoryMatch.Exact("nikt", [Admin, Person]));

    [Theory]
    [InlineData(@"CORP\jkowalski", "jkowalski")]
    [InlineData("jkowalski", "jkowalski")]
    [InlineData(@"CORP\", @"CORP\")]
    public void The_domain_is_stripped_only_when_something_follows_it(string name, string expected) =>
        Assert.Equal(expected, DirectoryMatch.Bare(name));
}
