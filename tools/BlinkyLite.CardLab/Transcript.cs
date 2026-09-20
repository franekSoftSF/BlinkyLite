using System.Reflection;
using System.Text;
using BlinkyLite.Contracts;
using Serilog;

namespace BlinkyLite.CardLab;

/// <summary>
/// Everything the run printed, kept so it can be written to the report, and
/// everything it knew, written to a log file next to it.
/// </summary>
/// <remarks>
/// <para>
/// The test station is not this machine: whoever runs the tool sends a file
/// back, not a screenshot of a console window that has already scrolled. So
/// every line goes to all three places at once - screen, report, log - and the
/// timestamps are the ones the operator saw.
/// </para>
/// <para>
/// The report and the log are different documents on purpose. The report is
/// the answer: short, readable, safe to send. The log is the evidence: every
/// detail nobody wants to read until something fails, and then the only thing
/// that matters, because the failure happened on a machine I cannot look at.
/// </para>
/// </remarks>
internal sealed class Transcript
{
    private readonly StringBuilder lines = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;

    private Transcript()
    {
    }

    /// <summary>Where the log of this run is being written.</summary>
    public string LogPath { get; private init; } = "-";

    /// <summary>
    /// Opens the log for one run and writes down what the run started from.
    /// </summary>
    /// <remarks>
    /// The arguments are logged whole. It is safe by construction: a PIN can
    /// never be one of them - there is no cmdlet parameter and no switch that
    /// carries a PIN anywhere in BlinkyLite, which is a rule, not an accident.
    /// </remarks>
    public static Transcript Start(string command, string[] args, string directory)
    {
        var path = Path.Combine(directory, $"cardlab-{DateTime.Now:yyyyMMdd-HHmmss}-{command}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(path,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                encoding: new UTF8Encoding(false))
            .CreateLogger();

        Log.Information("BlinkyLite CardLab {Version}", Assembly.GetEntryAssembly()?.GetName().Version);
        Log.Information("komenda: {Command} {Arguments}", command, string.Join(' ', args.Skip(1)));
        Log.Information("stacja: {Machine}, uzytkownik: {Domain}\\{User}",
            Environment.MachineName, Environment.UserDomainName, Environment.UserName);
        Log.Information("system: {Os}, proces {Bits}-bit, {Processors} rdzeni",
            Environment.OSVersion.VersionString, Environment.Is64BitProcess ? 64 : 32,
            Environment.ProcessorCount);
        Log.Information("jezyk komunikatow: {Culture}", Strings.Current.Culture.Name);

        // A station whose machine name is its domain name is not in a domain,
        // and that is the whole answer to half the ways EOBO can fail.
        Log.Information("w domenie: {Joined}",
            Environment.UserDomainName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? "NIE (nazwa domeny to nazwa maszyny)"
                : "tak");

        return new Transcript { LogPath = path };
    }

    /// <summary>Prints a line, keeps it for the report and logs it.</summary>
    public void Say(string line)
    {
        Console.WriteLine(line);
        lines.AppendLine(line);

        if (line.Length > 0)
        {
            Log.Information("{Line}", line);
        }
    }

    /// <summary>Prints and keeps a line of the step log, with the time it happened.</summary>
    public void Step(string line)
    {
        var stamp = (DateTimeOffset.UtcNow - started).TotalSeconds.ToString("00.0");

        Console.WriteLine($"  {line}");
        lines.AppendLine($"  +{stamp}s  {line}");
        Log.Information("krok: {Line}", line);
    }

    /// <summary>Prints to stderr, keeps it and logs it: a failed run is the interesting one.</summary>
    public void Problem(string line)
    {
        Console.Error.WriteLine(line);
        lines.AppendLine($"!! {line}");
        Log.Error("{Line}", line);
    }

    /// <summary>Same, with the exception that caused it - for the log only.</summary>
    public void Problem(string line, Exception e)
    {
        Console.Error.WriteLine(line);
        lines.AppendLine($"!! {line}");
        Log.Error(e, "{Line}", line);
    }

    /// <summary>
    /// For the log and nothing else: what was read, what was compared, what a
    /// COM object answered. Too much for the screen, exactly enough when the
    /// run has already failed on a machine I cannot look at.
    /// </summary>
    public void Detail(string message, params object?[] values) => Log.Debug(message, values);

    /// <summary>A step name from the message catalogue, in the station's language.</summary>
    public void Key(string messageKey) => Step(Strings.Current[messageKey]);

    public override string ToString() => lines.ToString();
}
