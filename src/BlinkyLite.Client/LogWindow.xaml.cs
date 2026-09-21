using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using BlinkyLite.Client.Localisation;

namespace BlinkyLite.Client;

/// <summary>
/// The client's own log, on the screen (the owner asked where it is).
/// </summary>
/// <remarks>
/// <para>
/// Read with <see cref="FileShare.ReadWrite"/>: Serilog keeps the file open for
/// as long as the application runs, and a plain read would fail with "in use"
/// on exactly the file somebody wants to look at.
/// </para>
/// <para>
/// Only the tail: a day of issuing is a few hundred lines, and a text box with
/// a week of them in it is a window that takes seconds to open.
/// </para>
/// </remarks>
public partial class LogWindow : Window
{
    public const int Lines = 1000;

    public LogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    /// <summary>The newest client-*.log, or null when there is none yet.</summary>
    public static string? CurrentFile()
    {
        try
        {
            return new DirectoryInfo(App.LogDirectory)
                .EnumerateFiles("client-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Load()
    {
        var file = CurrentFile();
        FileText.Text = file ?? App.LogDirectory;

        if (file is null)
        {
            LogText.Text = Text.Of("client.log.empty");
            return;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var tail = new Queue<string>(Lines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == Lines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            LogText.Text = tail.Count == 0 ? Text.Of("client.log.empty") : string.Join(Environment.NewLine, tail);
            LogText.ScrollToEnd();
            Status.Text = "";
        }
        catch (IOException e)
        {
            LogText.Text = e.Message;
        }
    }

    private void Refreshed(object sender, RoutedEventArgs e) => Load();

    private void Copied(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LogText.Text);
        Status.Text = Text.Of("client.log.copied");
    }

    private void FolderOpened(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.LogDirectory}\"") { UseShellExecute = true });

    private void Closed_(object sender, RoutedEventArgs e) => Close();
}
