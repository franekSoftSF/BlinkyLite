using System.Globalization;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;

namespace BlinkyLite.CardLab;

/// <summary>Command line of the bench tool.</summary>
internal sealed record Options(
    string Subject,
    string? Reader,
    string OutDirectory,
    string? Language,
    bool Yes,
    bool PinFromStdin,
    bool NoYkman)
{
    public static Options Parse(string[] args) => new(
        Subject: Value(args, "--subject") ?? "CN=BlinkyLite bench",
        Reader: Value(args, "--reader"),
        OutDirectory: Value(args, "--out") ?? Directory.GetCurrentDirectory(),
        Language: Value(args, "--lang"),
        Yes: args.Contains("--yes", StringComparer.Ordinal),
        PinFromStdin: args.Contains("--pin-from-stdin", StringComparer.Ordinal),
        NoYkman: args.Contains("--no-ykman", StringComparer.Ordinal));

    /// <summary>
    /// The station speaks its own language unless told otherwise, because the
    /// person typing the PIN is standing at it.
    /// </summary>
    public void ApplyLanguage()
    {
        if (Language is { } wanted)
        {
            Strings.Current.Culture = Strings.Pick(CultureInfo.GetCultureInfo(wanted));
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>
/// The PIN, typed by the person the key is for, without echo and twice.
/// This is what the WPF window will do in 0023; here it is a console.
/// </summary>
/// <remarks>
/// The words come from the message catalogue, not from this file: the person
/// at the bench reads the same sentences the client will show them, so a bad
/// sentence is found here rather than after the client is written.
/// </remarks>
internal sealed class ConsolePinPrompt : IPinPrompt
{
    private const int Attempts = 3;

    public Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default)
    {
        var strings = Strings.Current;

        Console.WriteLine();

        if (context.RefusalKey is { } refusal)
        {
            Console.WriteLine($"  {strings[refusal]}");
            Console.WriteLine($"  {strings.Format("pin.attempts-left", Attempts - context.Attempt + 1)}");
        }
        else
        {
            Console.WriteLine($"{strings["pin.title"]} ({context.Serial})");
            Console.WriteLine(strings["pin.explain"]);
            Console.WriteLine(strings["pin.rule.length"]);
        }

        var first = Read($"{strings["pin.new"]}: ");
        if (first is null)
        {
            return Task.FromResult<string?>(null);
        }

        var again = Read($"{strings["pin.repeat"]}: ");
        if (again is null)
        {
            return Task.FromResult<string?>(null);
        }

        if (!string.Equals(first, again, StringComparison.Ordinal))
        {
            // Not an attempt as the engine counts them: the engine judges PINs,
            // and it never saw one here.
            Console.WriteLine($"  {strings["pin.rule.mismatch"]}");
            return AskAsync(context, ct);
        }

        return Task.FromResult<string?>(first);
    }

    private static string? Read(string label)
    {
        Console.Write(label);
        var typed = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return typed.ToString();

                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;

                case ConsoleKey.Backspace when typed.Length > 0:
                    typed.Length--;
                    Console.Write("\b \b");
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        typed.Append(key.KeyChar);
                        Console.Write('*');
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// Reads the PIN from standard input. For an unattended bench run only: a PIN
/// that arrives this way was not typed by the person the key belongs to, and
/// that is the whole point of the other prompt.
/// </summary>
internal sealed class StdinPinPrompt : IPinPrompt
{
    public Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default)
    {
        if (context.RefusalKey is { } refusal)
        {
            Console.Error.WriteLine(Strings.Current[refusal]);
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(Console.ReadLine()?.Trim());
    }
}
