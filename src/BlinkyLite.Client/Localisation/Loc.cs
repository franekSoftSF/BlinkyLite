using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using BlinkyLite.Contracts;

namespace BlinkyLite.Client.Localisation;

/// <summary>
/// <c>{l:Loc pin.title}</c> in XAML: the text for a key, in the language the
/// window is currently in.
/// </summary>
/// <remarks>
/// <para>
/// Every human-readable string in this client goes through here. A literal in
/// XAML is a string that exists in one language and that no test can find
/// (docs/08).
/// </para>
/// <para>
/// It binds rather than reads, so the whole window changes language when
/// <see cref="Strings.Culture"/> changes - which is what the switch in the PIN
/// window is for: the operator and the person holding the card do not have to
/// read the same language.
/// </para>
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public LocExtension()
        : this("")
    {
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Strings.Current,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}

/// <summary>Turns a message key into text for code-behind and view models.</summary>
public static class Text
{
    public static string Of(string key) => Strings.Current[key];

    public static string Of(string key, params object?[] args) => Strings.Current.Format(key, args);

    /// <summary>The languages the switch offers, with their own names.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages =>
    [
        .. Strings.Supported.Select(code =>
            (code, CultureInfo.GetCultureInfo(code).NativeName)),
    ];
}
