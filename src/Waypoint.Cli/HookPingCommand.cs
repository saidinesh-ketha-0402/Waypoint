using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Waypoint.Cli;

public static class HookPingCommand
{
  public static async Task<int> ExecuteAsync(Stream stdin, Stream stdout, CancellationToken cancellationToken = default)
  {
    HookPingPayload? payload;
    using (var reader = new StreamReader(stdin, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
    {
      var json = await reader.ReadToEndAsync(cancellationToken);
      payload = JsonSerializer.Deserialize(json, HookPingJsonContext.Default.HookPingPayload);
    }

    var repoRoot = payload?.WorkspaceRoots?.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(repoRoot))
    {
      Console.Error.WriteLine("Missing workspace_roots[0] in hook payload.");
      return 1;
    }

    var probeDirectory = Path.Combine(repoRoot, ".git", "waypoint");
    Directory.CreateDirectory(probeDirectory);
    var dbPath = Path.Combine(probeDirectory, "probe.db");

    await using var connection = new SqliteConnection($"Data Source={dbPath}");
    await connection.OpenAsync(cancellationToken);

    await using (var pragmaCommand = connection.CreateCommand())
    {
      pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
      await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var createCommand = connection.CreateCommand())
    {
      createCommand.CommandText = """
        CREATE TABLE IF NOT EXISTS probe (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          created_utc TEXT NOT NULL,
          hook_event_name TEXT NULL,
          session_id TEXT NULL
        );
        """;
      await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var insertCommand = connection.CreateCommand())
    {
      insertCommand.CommandText = """
        INSERT INTO probe (created_utc, hook_event_name, session_id)
        VALUES ($created_utc, $hook_event_name, $session_id);
        """;
      insertCommand.Parameters.AddWithValue("$created_utc", DateTime.UtcNow.ToString("O"));
      insertCommand.Parameters.AddWithValue("$hook_event_name", payload?.HookEventName ?? string.Empty);
      insertCommand.Parameters.AddWithValue("$session_id", payload?.SessionId ?? string.Empty);
      await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var writer = new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
    await writer.WriteAsync("{}");
    await writer.FlushAsync(cancellationToken);

    return 0;
  }
}

public sealed class HookPingPayload
{
  [JsonPropertyName("workspace_roots")]
  public string[]? WorkspaceRoots { get; init; }

  [JsonPropertyName("hook_event_name")]
  public string? HookEventName { get; init; }

  [JsonPropertyName("session_id")]
  public string? SessionId { get; init; }
}

[JsonSerializable(typeof(HookPingPayload))]
public sealed partial class HookPingJsonContext : JsonSerializerContext;
