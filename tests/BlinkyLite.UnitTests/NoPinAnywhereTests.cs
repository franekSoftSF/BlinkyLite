using System.Reflection;
using System.Text.RegularExpressions;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The PIN belongs to the user and to the card, and to nothing else: no DTO
/// field, no entity property, no column (docs/03-data-model.md). In Blinky a
/// PIN once reached a column and a backup from there.
/// </summary>
public sealed partial class NoPinAnywhereTests
{
    // PascalCase word boundary, so Mapping and Shipping are not matches.
    [GeneratedRegex(@"(^|[a-z0-9])Pin($|[A-Z])")]
    private static partial Regex PinWord { get; }

    /// <summary>PIN policy is an attestation value (Once, Always), not a PIN.</summary>
    private static readonly string[] Allowed = ["PinPolicy", "TouchPolicy"];

    public static TheoryData<string> Types()
    {
        var data = new TheoryData<string>();
        foreach (var type in new[] { typeof(Role).Assembly, typeof(Procedures).Assembly }
                     .SelectMany(a => a.GetTypes())
                     .Where(t => t.IsPublic && !t.IsEnum))
        {
            data.Add(type.AssemblyQualifiedName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void No_public_type_carries_a_PIN(string typeName)
    {
        var type = Type.GetType(typeName)!;

        var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(p => p.Name)
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(f => f.Name))
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.Name ?? ""))
            .Where(name => !Allowed.Contains(name, StringComparer.Ordinal))
            .Where(name => PinWord.IsMatch(name))
            .Distinct()
            .ToList();

        Assert.Empty(members);
    }

    [Fact]
    public void The_migrations_declare_no_column_that_holds_a_PIN()
    {
        var columns = Migrations.Embedded
            .SelectMany(m => Regex.Matches(m.Sql, @"^\s{4}(?<name>[a-z_]+)\s", RegexOptions.Multiline))
            .Select(match => match.Groups["name"].Value)
            // Function arguments are the same names with a p_ prefix; both are
            // worth checking, and pin_policy is the attestation value.
            .Select(name => name.StartsWith("p_", StringComparison.Ordinal) ? name[2..] : name)
            .Where(name => name.Contains("pin", StringComparison.Ordinal) && name != "pin_policy")
            .Distinct()
            .ToList();

        Assert.Empty(columns);
    }
}
