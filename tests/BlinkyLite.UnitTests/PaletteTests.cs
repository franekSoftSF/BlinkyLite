using BlinkyLite.Client.Theme;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The colours of the client, counted rather than admired (D-20).
/// </summary>
/// <remarks>
/// A person stands at this window while somebody else types a PIN into it.
/// "Looks fine on my screen" is how the unreadable grey label ships, so every
/// pair that carries words is measured against WCAG 2.1.
/// </remarks>
public sealed class PaletteTests
{
    private const double Readable = 4.5;

    public static TheoryData<string, string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string, string>();

        foreach (var palette in Palette.All)
        {
            foreach (var pair in palette.TextPairs)
            {
                data.Add(palette.Name, pair.What, pair.Foreground.Hex, pair.Background.Hex);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_pair_that_carries_words_is_readable(
        string theme, string what, string foreground, string background)
    {
        var contrast = Palette.Contrast(new Colour(foreground), new Colour(background));

        Assert.True(contrast >= Readable,
            $"{theme}: {what} ({foreground} na {background}) ma {contrast:0.0}:1, a potrzeba {Readable}:1");
    }

    [Fact]
    public void The_accent_is_the_one_from_the_decision()
    {
        Assert.Equal("#1DB954", Palette.Light.AccentFill.Hex);
        Assert.Equal("#1DB954", Palette.Dark.AccentFill.Hex);
    }

    [Fact]
    public void White_on_the_accent_is_the_thing_we_do_not_do()
    {
        // Written down as a test because it is the obvious thing to reach for
        // and it fails people who need contrast: 2,6:1.
        var white = new Colour("#FFFFFF");

        Assert.True(Palette.Contrast(white, Palette.Light.AccentFill) < Readable);
        Assert.True(Palette.Contrast(Palette.Light.AccentFill, white) < Readable);
    }
}
