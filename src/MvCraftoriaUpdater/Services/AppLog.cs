using System.Text;

namespace MvCraftoriaUpdater.Services;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MV Craftoria Updater",
        "logs");

    internal static string CurrentLogPath { get; } = Path.Combine(
        LogDirectory,
        $"updater-{DateTime.Now:yyyyMMdd}.log");

    internal static void Info(string message) => Write("INFO", message, null);
    internal static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var text = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(' ')
                    .Append(level)
                    .Append(' ')
                    .AppendLine(message);
                if (exception is not null) text.AppendLine(exception.ToString());
                File.AppendAllText(CurrentLogPath, text.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never block recovery or shutdown.
        }
    }
}
