using System.Xml.Linq;

namespace BlinkyLite.UnitTests;

/// <summary>
/// Conventions from CLAUDE.md that a reviewer would otherwise have to remember.
/// </summary>
public sealed class RepositoryConventionTests
{
    private static readonly DirectoryInfo Root = FindRoot();

    public static TheoryData<string> Projects()
    {
        var data = new TheoryData<string>();
        foreach (var file in Root.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
                     .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            data.Add(Path.GetRelativePath(Root.FullName, file.FullName));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Package_versions_live_only_in_Directory_Packages_props(string project)
    {
        var document = XDocument.Load(Path.Combine(Root.FullName, project));

        var pinnedLocally = document.Descendants("PackageReference")
            .Where(r => r.Attribute("Version") is not null || r.Attribute("VersionOverride") is not null)
            .Select(r => r.Attribute("Include")?.Value)
            .ToList();

        Assert.Empty(pinnedLocally);
    }

    [Theory]
    [InlineData("src/BlinkyLite.Client/BlinkyLite.Client.csproj")]
    [InlineData("src/BlinkyLite.PowerShell/BlinkyLite.PowerShell.csproj")]
    public void Projects_that_show_text_to_people_turn_invariant_globalization_off(string project)
    {
        var document = XDocument.Load(Path.Combine(Root.FullName, project));

        var value = document.Descendants("InvariantGlobalization").SingleOrDefault()?.Value;

        Assert.Equal("false", value);
    }

    [Fact]
    public void Client_publishes_for_x64_and_arm64()
    {
        var document = XDocument.Load(Path.Combine(Root.FullName, "src/BlinkyLite.Client/BlinkyLite.Client.csproj"));

        var identifiers = document.Descendants("RuntimeIdentifiers").Single().Value.Split(';');

        Assert.Equal(["win-arm64", "win-x64"], identifiers.Order());
    }

    private static DirectoryInfo FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlinkyLite.slnx")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("BlinkyLite.slnx not found above the test output directory.");
    }
}
