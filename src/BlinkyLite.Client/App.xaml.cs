using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BlinkyLite.Client.Theme;
using BlinkyLite.Contracts;
using Serilog;

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

        // Under the operator's own profile, so no installer and no rights are
        // needed for a log to exist at all.
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlinkyLite");
        Directory.CreateDirectory(directory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(directory, "client-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                encoding: new UTF8Encoding(false))
            .CreateLogger();

        Log.Information("BlinkyLite.Client {Version} na {Machine}, uzytkownik {Domain}-{User}, jezyk {Culture}",
            typeof(App).Assembly.GetName().Version, Environment.MachineName,
            Environment.UserDomainName, Environment.UserName, Strings.Current.Culture.Name);

        // Anything that reaches here would otherwise close the window with no
        // trace of why.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Nieobsluzony blad w oknie");
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
