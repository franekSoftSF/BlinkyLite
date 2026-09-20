namespace BlinkyLite.Client.Theme;

/// <summary>One colour, as the hex somebody can paste into a design tool.</summary>
public readonly record struct Colour(string Hex)
{
    public byte R => Convert.ToByte(Hex.Substring(1, 2), 16);

    public byte G => Convert.ToByte(Hex.Substring(3, 2), 16);

    public byte B => Convert.ToByte(Hex.Substring(5, 2), 16);

    public override string ToString() => Hex;
}

/// <summary>A pair the eye has to separate, and what it is for.</summary>
public readonly record struct TextPair(string What, Colour Foreground, Colour Background);

/// <summary>
/// The two themes, as data rather than as XAML.
/// </summary>
/// <remarks>
/// <para>
/// In C# because a test has to be able to read every pair and count the
/// contrast (D-20). Colours written straight into XAML cannot be checked by
/// anything, and "looks fine on my screen" is how the unreadable grey label
/// gets shipped.
/// </para>
/// <para>
/// The accent is <c>#1DB954</c>, and its lightness only works in one
/// direction: as a fill it needs black on top (8,1:1), and as text it has to be
/// darkened to <c>#0E7A38</c> on white (5,4:1) or lightened to <c>#3DDB74</c>
/// on the dark background (9,6:1). White on the accent is 2,6:1 and the accent
/// as text on white is the same - neither is used anywhere.
/// </para>
/// </remarks>
public sealed record Palette(
    string Name,
    Colour Background,
    Colour Surface,
    Colour Text,
    Colour MutedText,
    Colour Border,
    Colour AccentFill,
    Colour OnAccent,
    Colour AccentText,
    Colour Danger,
    Colour Warning)
{
    public static readonly Palette Light = new(
        Name: "light",
        Background: new("#FFFFFF"),
        Surface: new("#F3F3F3"),
        Text: new("#1A1A1A"),
        MutedText: new("#595959"),
        Border: new("#C8C8C8"),
        AccentFill: new("#1DB954"),
        OnAccent: new("#000000"),
        AccentText: new("#0E7A38"),
        Danger: new("#A4262C"),
        Warning: new("#7A4A00"));

    public static readonly Palette Dark = new(
        Name: "dark",
        Background: new("#1A1A1A"),
        Surface: new("#242424"),
        Text: new("#F2F2F2"),
        MutedText: new("#B4B4B4"),
        Border: new("#3D3D3D"),
        AccentFill: new("#1DB954"),
        OnAccent: new("#000000"),
        AccentText: new("#3DDB74"),
        Danger: new("#FF8A85"),
        Warning: new("#F0B429"));

    public static IReadOnlyList<Palette> All => [Light, Dark];

    /// <summary>
    /// Every pair of colours that carries words, for the test that counts them.
    /// </summary>
    /// <remarks>
    /// Borders and fills are not here: WCAG asks 4,5:1 of text, and a border
    /// that met it would make the window look like a spreadsheet.
    /// </remarks>
    public IReadOnlyList<TextPair> TextPairs =>
    [
        new("tekst na tle", Text, Background),
        new("tekst na powierzchni", Text, Surface),
        new("tekst drugorzedny na tle", MutedText, Background),
        new("tekst drugorzedny na powierzchni", MutedText, Surface),
        new("napis na przycisku akcentowym", OnAccent, AccentFill),
        new("tekst akcentowy na tle", AccentText, Background),
        new("tekst akcentowy na powierzchni", AccentText, Surface),
        new("blad na tle", Danger, Background),
        new("blad na powierzchni", Danger, Surface),
        new("ostrzezenie na tle", Warning, Background),
        new("ostrzezenie na powierzchni", Warning, Surface),
    ];

    /// <summary>WCAG 2.1 contrast ratio, 1:1 to 21:1.</summary>
    public static double Contrast(Colour a, Colour b)
    {
        var first = Luminance(a);
        var second = Luminance(b);

        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);

        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Colour colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        var part = value / 255.0;

        return part <= 0.03928 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4);
    }
}
