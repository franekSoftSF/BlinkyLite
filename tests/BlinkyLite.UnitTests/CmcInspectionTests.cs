using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.UnitTests;

/// <summary>
/// Reading back the one field that decides who gets the certificate.
/// </summary>
/// <remarks>
/// The first CMC CertEnroll built on a real station read as
/// <c>DW-AD%5Cjan.kowalski</c> and was reported as a mismatch. The name
/// was right; the reading was wrong. These are the shapes that answer has.
/// </remarks>
public sealed class CmcInspectionTests
{
    [Theory]
    // What CertEnroll really writes: the backslash percent-encoded, because
    // the pairs are joined by & and =, and it has to survive them.
    [InlineData("requestername=DW-AD%5Cjan.kowalski", @"DW-AD\jan.kowalski")]
    [InlineData("requestername=DW-AD\\jan.kowalski", @"DW-AD\jan.kowalski")]
    [InlineData("certificatetemplate=User&requestername=CORP%5Cjkowalski", @"CORP\jkowalski")]
    [InlineData("requestername=CORP%5Cann%20lee", "CORP\\ann lee")]
    public void The_requester_name_comes_out_decoded(string regInfo, string expected) =>
        Assert.Equal(expected, CmcInspection.RequesterFrom(regInfo));

    [Fact]
    public void A_RegInfo_without_a_requester_gives_back_what_it_had()
    {
        // Returned whole rather than as null: "there is something here and it
        // is not a requester name" is worth seeing in a report.
        const string other = "certificatetemplate=User";

        Assert.Equal(other, CmcInspection.RequesterFrom(other));
    }

    [Fact]
    public void Nothing_that_is_not_a_CMC_is_read_as_one()
    {
        var contents = CmcInspection.Inspect([1, 2, 3, 4]);

        Assert.NotNull(contents.Problem);
        Assert.False(contents.IsPkiData);
        Assert.Null(contents.RequesterName);
    }
}
