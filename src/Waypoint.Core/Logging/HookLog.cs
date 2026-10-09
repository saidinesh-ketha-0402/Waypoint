using System.Text.Json;

namespace Waypoint.Core.Logging;

public static class HookLog
{
  public static void Append(string logPath, string eventName, object? details = null)
  {
    if (string.IsNullOrWhiteSpace(logPath))
    {
      throw new ArgumentException("Log path is required.", nameof(logPath));
    }

    var directory = Path.GetDirectoryName(logPath)
      ?? throw new InvalidOperationException("Log path directory is missing.");
    Directory.CreateDirectory(directory);

    var now = DateTimeOffset.UtcNow.ToString("O");
    var payload = details is null
      ? "{}"
      : JsonSerializer.Serialize(details);

    File.AppendAllText(logPath, $"{now} {eventName} {payload}{Environment.NewLine}");
  }
}
