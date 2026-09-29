using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BlinkyLite.Contracts;
using BlinkyLite.Ui.Theme;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// The unlock tool: one window, no sign-in, no server.
/// </summary>
/// <remarks>
/// Everything it needs is in front of the person using it - the token in the
/// reader and the PUK the helpdesk read out. Nothing is remembered between
/// runs, because there is nothing worth remembering: an address would be, and
/// this application has none.
/// </remarks>
public partial class App : Application
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlinkyLite");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Strings.Current.Culture = Strings.Pick(CultureInfo.CurrentUICulture);
        ThemeManager.Apply(ThemeManager.FromWindows());

        Directory.CreateDirectory(LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDirectory, "unlock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                encoding: new UTF8Encoding(false))
            .CreateLogger();

        Log.Information("BlinkyLite.Unlock {Version} na {Machine}, uzytkownik {Domain}-{User}, jezyk {Culture}",
            typeof(App).Assembly.GetName().Version, Environment.MachineName,
            Environment.UserDomainName, Environment.UserName, Strings.Current.Culture.Name);

        DispatcherUnhandledException += (_, args) => Log.Fatal(args.Exception, "Nieobsluzony blad w oknie");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
