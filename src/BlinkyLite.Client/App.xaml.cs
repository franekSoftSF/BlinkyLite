using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BlinkyLite.Client.Theme;
using BlinkyLite.Contracts;
using Serilog;

namespace BlinkyLite.Client;

/// <summary>
/// Starts the client in the language and theme it was left in.
/// </summary>
/// <remarks>
/// Both come from <see cref="ClientSettings"/> when they were chosen before,
/// and from Windows when they were not. Nothing secret is remembered: the
/// settings file holds an address, a login, a language and a theme.
/// </remarks>
public partial class App : Application
{
    /// <summary>What was remembered from last time; the window updates it.</summary>
    public static ClientSettings Settings { get; set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = ClientSettings.Load();

        Strings.Current.Culture = Settings.Language is { } language
            ? CultureInfo.GetCultureInfo(language)
            : Strings.Pick(CultureInfo.CurrentUICulture);

        ThemeManager.Apply(Settings.Theme switch
        {
            "light" => Theme.Palette.Light,
            "dark" => Theme.Palette.Dark,
            _ => ThemeManager.FromWindows(),
        });

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
