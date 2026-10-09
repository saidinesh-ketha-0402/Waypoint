using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookPingCommandTests
{
  [Fact]
  public async Task Ping_writes_probe_row_and_returns_empty_json()
  {
    var repoRoot = CreateTempRepoRoot();
    var payload = CreatePayload(repoRoot);

    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();

    var exitCode = await HookPingCommand.ExecuteAsync(input, output);

    Assert.Equal(0, exitCode);
    Assert.Equal("{}", Encoding.UTF8.GetString(output.ToArray()));

    await AssertProbeRowInsertedAsync(repoRoot);
  }

  [Fact]
  public async Task Ping_cli_process_reads_stdin_and_writes_probe_row()
  {
    var repoRoot = CreateTempRepoRoot();
    var payload = CreatePayload(repoRoot);
    var cliAssemblyPath = typeof(HookPingCommand).Assembly.Location;

    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "dotnet",
        Arguments = $"\"{cliAssemblyPath}\" hook ping",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      }
    };

    process.Start();
    await process.StandardInput.WriteAsync(payload);
    process.StandardInput.Close();

    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    Assert.Equal(0, process.ExitCode);
    Assert.Equal("{}", stdout);
    Assert.Equal(string.Empty, stderr);
    await AssertProbeRowInsertedAsync(repoRoot);
  }

  private static string CreateTempRepoRoot()
  {
    var repoRoot = Path.Combine(Path.GetTempPath(), $"waypoint-probe-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
    return repoRoot;
  }

  private static string CreatePayload(string repoRoot)
  {
    return $$"""
      {
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "sessionStart",
        "session_id": "sess_test_001"
      }
      """;
  }

  private static async Task AssertProbeRowInsertedAsync(string repoRoot)
  {
    var dbPath = Path.Combine(repoRoot, ".git", "waypoint", "probe.db");
    Assert.True(File.Exists(dbPath));

    await using var connection = new SqliteConnection($"Data Source={dbPath}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM probe;";
    var rowCount = Convert.ToInt32(await command.ExecuteScalarAsync());
    Assert.True(rowCount >= 1);
  }
}
