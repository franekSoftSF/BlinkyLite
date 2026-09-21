using System.Text.RegularExpressions;
using BlinkyLite.Client.Theme;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The web console's colours are the WPF client's colours, value for value.
/// </summary>
/// <remarks>
/// PaletteTests counts the contrast of Palette.cs; that guarantee means
/// nothing for the browser unless styles.scss says the same thing. Two copies
/// of one palette drift the first time somebody tunes one of them, and the
/// console that shows PUKs is the one that would drift unnoticed.
/// </remarks>
public sealed partial class WebPaletteTests
{
    [GeneratedRegex(@"--bl-(?<name>[a-z-]+):\s*(?<hex>#[0-9A-Fa-f]{6})\s*;")]
    private static partial Regex Token { get; }

    public static TheoryData<string> Themes() => ["light", "dark"];

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_web_colour_is_the_palette_colour(string theme)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryConventionTests.Root.FullName, "web/src/styles.scss"));
        var block = Regex.Match(css, $@"/\* palette:{theme} \*/(?<body>.*?)/\* /palette:{theme} \*/", RegexOptions.Singleline);
        Assert.True(block.Success, $"styles.scss has no palette:{theme} block");

        var web = Token.Matches(block.Groups["body"].Value)
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["hex"].Value.ToUpperInvariant());

        var palette = theme == "light" ? Palette.Light : Palette.Dark;
        var expected = new Dictionary<string, Colour>
        {
            ["background"] = palette.Background,
            ["surface"] = palette.Surface,
            ["text"] = palette.Text,
            ["muted"] = palette.MutedText,
            ["border"] = palette.Border,
            ["accent-fill"] = palette.AccentFill,
            ["on-accent"] = palette.OnAccent,
            ["accent-text"] = palette.AccentText,
            ["danger"] = palette.Danger,
            ["warning"] = palette.Warning,
        };

        foreach (var (name, colour) in expected)
        {
            Assert.True(web.TryGetValue(name, out var hex), $"{theme}: --bl-{name} is missing from styles.scss");
            Assert.Equal(colour.Hex.ToUpperInvariant(), hex);
        }
    }
}
