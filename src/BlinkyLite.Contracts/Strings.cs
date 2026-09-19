using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace BlinkyLite.Contracts;

/// <summary>
/// The one catalogue of human-readable text, shared by the WPF client, the
/// PowerShell module and (for the expiry mail, after 1.0) the server. Keys are
/// stable, the text is not; the server sends keys and never sentences
/// (docs/08-localization.md).
/// </summary>
/// <remarks>
/// An indexer with <see cref="INotifyPropertyChanged"/>, so WPF can bind to
/// <c>[key]</c> and the whole window changes language without being rebuilt -
/// the pattern from Blinky's Agent UI, reading resources instead of a
/// dictionary in code.
/// </remarks>
public sealed class Strings : INotifyPropertyChanged
{
    /// <summary>What WPF calls an indexer in a PropertyChanged notification.</summary>
    public const string IndexerName = "Item[]";

    /// <summary>The four languages BlinkyLite speaks (D-12).</summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "de", "sv", "pl"];

    private static readonly ResourceManager Resources =
        new("BlinkyLite.Contracts.Resources.Messages", typeof(Strings).Assembly);

    private CultureInfo culture = Pick(CultureInfo.CurrentUICulture);

    public static Strings Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language in use; anything outside <see cref="Supported"/> falls back to English.</summary>
    public CultureInfo Culture
    {
        get => culture;
        set
        {
            var picked = Pick(value);
            if (Equals(picked, culture))
            {
                return;
            }

            culture = picked;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        }
    }

    /// <summary>The text for a key. An unknown key returns itself, so a miss is visible, not empty.</summary>
    public string this[string key] => Resources.GetString(key, culture) ?? key;

    /// <summary>Formats a key's text with its parameters in the current language.</summary>
    public string Format(string key, params object?[] args) => string.Format(culture, this[key], args);

    /// <summary>The text for an audit action code, e.g. <c>puk.disclosed</c>.</summary>
    public string Audit(string action) => this[$"audit.{action}"];

    public static CultureInfo Pick(CultureInfo requested)
    {
        var language = requested.TwoLetterISOLanguageName;
        return CultureInfo.GetCultureInfo(Supported.Contains(language) ? language : "en");
    }
}
