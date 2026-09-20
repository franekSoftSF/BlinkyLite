using System.Text;
using BlinkyLite.Contracts;

namespace BlinkyLite.CardLab;

/// <summary>
/// Everything the run printed, kept so it can be written to the report.
/// </summary>
/// <remarks>
/// The test station is not this machine: whoever runs the tool sends a file
/// back, not a screenshot of a console window that has already scrolled. So
/// every line goes to both places, once, and the timestamps are the ones the
/// operator saw.
/// </remarks>
internal sealed class Transcript
{
    private readonly StringBuilder lines = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;

    /// <summary>Prints a line and keeps it.</summary>
    public void Say(string line)
    {
        Console.WriteLine(line);
        lines.AppendLine(line);
    }

    /// <summary>Prints and keeps a line that belongs to the step log, with the time it happened.</summary>
    public void Step(string line)
    {
        var stamp = (DateTimeOffset.UtcNow - started).TotalSeconds.ToString("00.0");
        Console.WriteLine($"  {line}");
        lines.AppendLine($"  +{stamp}s  {line}");
    }

    /// <summary>Prints to stderr and keeps it: a report of a failed run is the interesting one.</summary>
    public void Problem(string line)
    {
        Console.Error.WriteLine(line);
        lines.AppendLine($"!! {line}");
    }

    /// <summary>A step name from the message catalogue, in the station's language.</summary>
    public void Key(string messageKey) => Step(Strings.Current[messageKey]);

    public override string ToString() => lines.ToString();
}
