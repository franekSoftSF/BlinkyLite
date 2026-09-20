using System.Text.RegularExpressions;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The module exports what it actually has.
/// </summary>
/// <remarks>
/// A binary module exports only what its manifest names. A cmdlet written and
/// not listed simply is not there, and the error that follows is "the term
/// New-Something is not recognised" - which reads like a typo rather than like
/// a missing line in a psd1.
/// </remarks>
public sealed partial class ModuleManifestTests
{
    [GeneratedRegex(@"\[Cmdlet\(([^)]*)\)\]", RegexOptions.Singleline)]
    private static partial Regex CmdletAttribute { get; }

    // PowerShell quotes with apostrophes; matching only double quotes found
    // nothing and called it a mismatch.
    [GeneratedRegex(@"'([A-Za-z]+-BlinkyLite[A-Za-z]*)'")]
    private static partial Regex Exported { get; }

    [GeneratedRegex(@"Verbs\w+\.(\w+),\s*""(\w+)""")]
    private static partial Regex VerbAndNoun { get; }

    [Fact]
    public void Every_cmdlet_in_the_code_is_in_the_manifest()
    {
        var root = RepositoryConventionTests.Root.FullName;
        var source = File.ReadAllText(Path.Combine(root, "src/BlinkyLite.PowerShell/Cmdlets.cs"));
        var manifest = File.ReadAllText(Path.Combine(root, "src/BlinkyLite.PowerShell/BlinkyLite.psd1"));

        var written = CmdletAttribute.Matches(source)
            .Select(m => VerbAndNoun.Match(m.Groups[1].Value))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value}-{m.Groups[2].Value}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var exported = Exported.Matches(manifest)
            .Select(m => m.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(written);
        Assert.Equal(string.Join(", ", written), string.Join(", ", exported));
    }
}
