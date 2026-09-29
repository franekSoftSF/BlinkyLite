using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BlinkyLite.Contracts;
using BlinkyLite.Ui.Theme;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// The unlock tool: one window, two ways in, and no sign-in either way.
/// </summary>
/// <remarks>
/// With the PUK in hand (0056) everything it needs is in front of the person:
/// the token in the reader and the digits the helpdesk read out. Over the
/// telephone (0057) it talks to the server, but still signs in to nothing -
/// what a blocked PIN means is having nothing to sign in with. The address is
/// the only thing kept between runs, and never a PUK, a PIN or a token.
/// </remarks>
public partial class App : Application
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlinkyLite");

    /// <summary>The server's address, if this station has one (0057).</summary>
    public static UnlockSettings Settings { get; set; } = new();

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

        // After the logger: a settings file nobody can read says so in the log.
        Settings = UnlockSettings.Load();

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
