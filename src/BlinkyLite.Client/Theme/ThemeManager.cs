using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlinkyLite.Client.Theme;

/// <summary>
/// Puts a <see cref="Palette"/> into the application's resources, so XAML
/// names roles (<c>Text</c>, <c>AccentFill</c>) and never colours.
/// </summary>
/// <remarks>
/// <para>
/// The brushes are built from the same C# the contrast test reads (D-20).
/// A colour typed into XAML would be a colour nothing can check.
/// </para>
/// <para>
/// The first choice comes from Windows, because an operator who has set their
/// machine to dark did not do it by accident. The switch is there because a
/// bright room at a service desk is a different thing from a preference.
/// </para>
/// </remarks>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static Palette Current { get; private set; } = Palette.Light;

    /// <summary>What Windows is set to, defaulting to light when it will not say.</summary>
    public static Palette FromWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Palette.Dark : Palette.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Palette.Light;
        }
    }

    public static void Apply(Palette palette)
    {
        Current = palette;

        var resources = Application.Current.Resources;

        Set(resources, "Background", palette.Background);
        Set(resources, "Surface", palette.Surface);
        Set(resources, "Text", palette.Text);
        Set(resources, "MutedText", palette.MutedText);
        Set(resources, "Border", palette.Border);
        Set(resources, "AccentFill", palette.AccentFill);
        Set(resources, "OnAccent", palette.OnAccent);
        Set(resources, "AccentText", palette.AccentText);
        Set(resources, "Danger", palette.Danger);
        Set(resources, "Warning", palette.Warning);
    }

    /// <summary>Switches to the other one; returns what it switched to.</summary>
    public static Palette Toggle()
    {
        var next = Current == Palette.Light ? Palette.Dark : Palette.Light;
        Apply(next);

        return next;
    }

    private static void Set(ResourceDictionary resources, string role, Colour colour)
    {
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();

        resources[role] = brush;
    }
}
