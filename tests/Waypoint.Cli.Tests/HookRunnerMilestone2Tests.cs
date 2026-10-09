using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookRunnerMilestone2Tests
{
  [Fact]
  public async Task BeforeSubmitPrompt_creates_task_links_session_and_records_prompt_episode()
  {
    var repoRoot = CreateTempRepoRoot();
    var payload = $$"""
      {
        "conversation_id": "conv_m2_001",
        "session_id": "sess_m2_001",
        "model": "gpt-5.3-codex",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "Implement checkout flow and keep tests passing."
      }
      """;

    var result = await ExecuteHookAsync("beforeSubmitPrompt", payload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{\"continue\":true}\n", result.StdOut);

    var dbPath = Path.Combine(repoRoot, ".git", "waypoint", "graph.db");
    await using var connection = new SqliteConnection($"Data Source={dbPath}");
    await connection.OpenAsync();

    await using (var taskCommand = connection.CreateCommand())
    {
      taskCommand.CommandText = "SELECT COUNT(*) FROM task WHERE status = 'active';";
      Assert.Equal(1L, await taskCommand.ExecuteScalarAsync());
    }

    await using (var sessionCommand = connection.CreateCommand())
    {
      sessionCommand.CommandText = "SELECT task_id FROM session WHERE id = 'sess_m2_001';";
      var taskId = await sessionCommand.ExecuteScalarAsync();
      Assert.NotNull(taskId);
      Assert.IsType<long>(taskId);
    }

    await using (var episodeCommand = connection.CreateCommand())
    {
      episodeCommand.CommandText = "SELECT kind FROM episode WHERE session_id = 'sess_m2_001' ORDER BY id DESC LIMIT 1;";
      Assert.Equal("prompt", await episodeCommand.ExecuteScalarAsync() as string);
    }
  }

  [Fact]
  public async Task SessionStart_without_active_task_returns_empty_json()
  {
    var repoRoot = CreateTempRepoRoot();
    var payload = $$"""
      {
        "conversation_id": "conv_m2_002",
        "session_id": "sess_m2_002",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "sessionStart"
      }
      """;

    var result = await ExecuteHookAsync("sessionStart", payload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);
  }

  [Fact]
  public async Task SessionStart_with_active_task_returns_brief_and_env()
  {
    var repoRoot = CreateTempRepoRoot();
    var promptPayload = $$"""
      {
        "conversation_id": "conv_m2_003",
        "session_id": "sess_m2_003",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task smoke test"
      }
      """;
    await ExecuteHookAsync("beforeSubmitPrompt", promptPayload);

    var startPayload = $$"""
      {
        "conversation_id": "conv_m2_003",
        "session_id": "sess_m2_004",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "sessionStart"
      }
      """;

    var result = await ExecuteHookAsync("sessionStart", startPayload);
    Assert.Equal(0, result.ExitCode);
    Assert.Contains("\"additional_context\":\"## Waypoint resume brief", result.StdOut, StringComparison.Ordinal);
    Assert.Contains("\"WAYPOINT_TASK_ID\":\"", result.StdOut, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SessionEnd_sets_ended_at_and_reason()
  {
    var repoRoot = CreateTempRepoRoot();
    var promptPayload = $$"""
      {
        "conversation_id": "conv_m2_004",
        "session_id": "sess_m2_005",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "Fix discount math edge case."
      }
      """;
    await ExecuteHookAsync("beforeSubmitPrompt", promptPayload);

    var endPayload = $$"""
      {
        "conversation_id": "conv_m2_004",
        "session_id": "sess_m2_005",
        "workspace_roots": ["{{repoRoot}}"],
        "hook_event_name": "sessionEnd",
        "final_status": "stopped"
      }
      """;

    var endResult = await ExecuteHookAsync("sessionEnd", endPayload);
    Assert.Equal(0, endResult.ExitCode);
    Assert.Equal("{}\n", endResult.StdOut);

    var dbPath = Path.Combine(repoRoot, ".git", "waypoint", "graph.db");
    await using var connection = new SqliteConnection($"Data Source={dbPath}");
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT ended_at, end_reason FROM session WHERE id = 'sess_m2_005';";
    await using var reader = await command.ExecuteReaderAsync();
    Assert.True(await reader.ReadAsync());
    Assert.False(reader.IsDBNull(0));
    Assert.Equal("stopped", reader.GetString(1));
  }

  [Fact]
  public async Task Garbage_input_uses_safe_defaults_and_exits_zero()
  {
    var beforeSubmit = await ExecuteHookAsync("beforeSubmitPrompt", "not json");
    Assert.Equal(0, beforeSubmit.ExitCode);
    Assert.Equal("{\"continue\":true}\n", beforeSubmit.StdOut);

    var sessionStart = await ExecuteHookAsync("sessionStart", "not json");
    Assert.Equal(0, sessionStart.ExitCode);
    Assert.Equal("{}\n", sessionStart.StdOut);
  }

  private static async Task<HookExecutionResult> ExecuteHookAsync(string eventName, string payload)
  {
    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();

    var exitCode = await HookRunner.RunAsync(
      eventName,
      input,
      output,
      new HookRunnerOptions());

    return new HookExecutionResult(exitCode, Encoding.UTF8.GetString(output.ToArray()));
  }

  private static string CreateTempRepoRoot()
  {
    var repoRoot = Path.Combine(Path.GetTempPath(), $"waypoint-hook-m2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
    return repoRoot;
  }

  private sealed record HookExecutionResult(int ExitCode, string StdOut);
}
