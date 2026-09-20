using BlinkyLite.Contracts;
using BlinkyLite.Issuance;

namespace BlinkyLite.CardLab;

/// <summary>Command line of the bench tool.</summary>
internal sealed record Options(
    string Subject,
    string? Reader,
    string? Out,
    bool Yes,
    bool PinFromStdin)
{
    public static Options Parse(string[] args) => new(
        Subject: Value(args, "--subject") ?? "CN=BlinkyLite bench",
        Reader: Value(args, "--reader"),
        Out: Value(args, "--out"),
        Yes: args.Contains("--yes", StringComparer.Ordinal),
        PinFromStdin: args.Contains("--pin-from-stdin", StringComparer.Ordinal));

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
internal sealed class ConsolePinPrompt : IPinPrompt
{
    public Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default)
    {
        if (context.RefusalKey is { } refusal)
        {
            Console.WriteLine($"  that PIN was refused: {refusal}");
        }

        Console.WriteLine();
        Console.WriteLine($"Set a PIN for token {context.Serial}. "
                          + $"{context.Policy.MinimumLength}-{context.Policy.MaximumLength} digits, "
                          + "and you will type it every time you use the key.");

        var first = Read("PIN: ");
        if (first is null)
        {
            return Task.FromResult<string?>(null);
        }

        var again = Read("PIN again: ");
        if (again is null)
        {
            return Task.FromResult<string?>(null);
        }

        if (!string.Equals(first, again, StringComparison.Ordinal))
        {
            Console.WriteLine("  the two PINs are not the same");
            return AskAsync(context with { Attempt = context.Attempt + 1, RefusalKey = "pin.rule.mismatch" }, ct);
        }

        return Task.FromResult<string?>(first);
    }

    private static string? Read(string label)
    {
        Console.Write(label);
        var typed = new System.Text.StringBuilder();

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
            Console.Error.WriteLine($"the PIN from standard input was refused: {refusal}");
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(Console.ReadLine()?.Trim());
    }
}
