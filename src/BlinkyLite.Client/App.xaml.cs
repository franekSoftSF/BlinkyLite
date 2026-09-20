using System.Globalization;
using System.Windows;
using BlinkyLite.Client.Theme;
using BlinkyLite.Contracts;

namespace BlinkyLite.Client;

/// <summary>
/// Starts the client in the operator's language and the machine's theme.
/// </summary>
/// <remarks>
/// Both can be changed in the window. Neither is remembered anywhere: this
/// client keeps no settings file, because the only state worth keeping is on
/// the server and on the card.
/// </remarks>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Strings.Current.Culture = Strings.Pick(CultureInfo.CurrentUICulture);
        ThemeManager.Apply(ThemeManager.FromWindows());
    }
}
