using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;

namespace BlinkyLite.UnitTests;

/// <summary>
/// A missing translation is a red build, not English text in a German window
/// (docs/08-localization.md). The .resx files are the source of truth here;
/// the ResourceManager side is checked as well, because a satellite assembly
/// that was never built would pass a file-only test.
/// </summary>
public sealed class MessageCatalogueTests
{
    private static readonly string ResourcesPath =
        Path.Combine(RepositoryConventionTests.Root.FullName, "src", "BlinkyLite.Contracts", "Resources");

    private static readonly Dictionary<string, Dictionary<string, string>> Catalogues = Load();

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Strings.Supported)
        {
            data.Add(language);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_every_key_and_none_of_them_is_empty(string language)
    {
        var english = Catalogues["en"];
        var catalogue = Catalogues[language];

        Assert.Empty(english.Keys.Except(catalogue.Keys));
        Assert.Empty(catalogue.Keys.Except(english.Keys));
        Assert.Empty(catalogue.Where(entry => string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Key));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_text_uses_the_same_parameters_in_every_language(string language)
    {
        var mismatched = Catalogues["en"]
            .Where(entry => Placeholders(entry.Value) != Placeholders(Catalogues[language][entry.Key]))
            .Select(entry => entry.Key)
            .ToList();

        Assert.Empty(mismatched);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_satellite_assemblies_are_really_built(string language)
    {
        var strings = new Strings { Culture = CultureInfo.GetCultureInfo(language) };

        // "Sign in" in four languages is four different words; if a satellite
        // assembly is missing, every language quietly returns English.
        var text = strings["common.sign-in"];

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(Catalogues[language]["common.sign-in"], text);
    }

    [Fact]
    public void Every_error_code_the_server_can_send_has_a_text()
    {
        var codes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith("error.", StringComparison.Ordinal))
            .ToList();
        var missing = codes.Where(code => !Catalogues["en"].ContainsKey(code)).ToList();

        Assert.NotEmpty(codes);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_audit_action_the_database_writes_has_a_text()
    {
        // Taken from the migrations rather than from a list kept by hand: a new
        // bl_* function with a new action would otherwise be invisible here.
        var actions = Migrations.Embedded
            .SelectMany(migration => Regex.Matches(migration.Sql, @"_bl_write_audit\(\s*'(?<action>[a-z.\-]+)'"))
            .Select(match => match.Groups["action"].Value)
            .Concat(["puk.disclosed", "mgmt-key.disclosed"])   // written as p_kind || '.disclosed'
            .Concat(["auth.login", "auth.denied"])             // bl_audit's whitelist
            .Distinct()
            .ToList();

        var missing = actions.Where(action => !Catalogues["en"].ContainsKey($"audit.{action}")).ToList();

        Assert.NotEmpty(actions);
        Assert.Empty(missing);
    }

    [Fact]
    public void Changing_the_language_tells_the_windows_to_repaint()
    {
        var strings = new Strings { Culture = CultureInfo.GetCultureInfo("en") };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)strings).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var before = strings["common.cancel"];
        strings.Culture = CultureInfo.GetCultureInfo("pl");

        Assert.Equal([nameof(Strings.Culture), Strings.IndexerName], changed);
        Assert.NotEqual(before, strings["common.cancel"]);
    }

    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("sv-FI", "sv")]
    [InlineData("pl-PL", "pl")]
    [InlineData("fr-FR", "en")]
    [InlineData("", "en")]
    public void An_unsupported_language_falls_back_to_English(string requested, string expected) =>
        Assert.Equal(expected, Strings.Pick(CultureInfo.GetCultureInfo(requested)).TwoLetterISOLanguageName);

    [Fact]
    public void An_unknown_key_shows_itself_instead_of_nothing() =>
        Assert.Equal("no.such.key", Strings.Current["no.such.key"]);

    [Fact]
    public void Every_term_in_the_glossary_is_written_down_before_it_is_used() =>
        Assert.True(File.Exists(Path.Combine(ResourcesPath, "GLOSSARY.md")));

    /// <summary>The parameters a text uses, as a comparable string: "{0},{1}".</summary>
    private static string Placeholders(string text) =>
        string.Join(',', Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Distinct().Order(StringComparer.Ordinal));

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var catalogues = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Strings.Supported)
        {
            var file = Path.Combine(ResourcesPath, language == "en" ? "Messages.resx" : $"Messages.{language}.resx");
            catalogues[language] = XDocument.Load(file).Root!.Elements("data")
                .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);
        }

        return catalogues;
    }
}

/// <summary>
/// Text a person reads belongs in Messages.resx, not in XAML or a cmdlet
/// (CLAUDE.md). This walks the sources the way a reviewer would.
/// </summary>
public sealed class NoHardCodedTextTests
{
    /// <summary>The product name is the same in every language.</summary>
    // The product name, and its two halves: the wordmark draws "Blinky" and
    // "Lite" in two colours (brand/README.md). A proper name, not a sentence.
    private static readonly string[] Allowed = ["BlinkyLite", "Blinky", "Lite"];

    [Fact]
    public void No_XAML_carries_text_for_a_person()
    {
        var literals = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryConventionTests.Root.FullName, "src"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"(?:Text|Content|Header|Title|ToolTip)=""(?<value>[^""{}]+)"""))
            {
                var value = match.Groups["value"].Value.Trim();
                if (value.Length > 1 && !Allowed.Contains(value, StringComparer.Ordinal))
                {
                    literals.Add($"{Path.GetFileName(file)}: {value}");
                }
            }
        }

        Assert.Empty(literals);
    }
}
