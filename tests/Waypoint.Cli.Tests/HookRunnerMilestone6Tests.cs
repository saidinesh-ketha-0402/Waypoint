using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookRunnerMilestone6Tests
{
  [Fact]
  public async Task PreCompact_checkpoint_is_idempotent_and_returns_user_message()
  {
    await using var fixture = await TempRepoFixture.CreateAsync("waypoint-cli-m6-precompact");
    var transcriptPath = Path.Combine(fixture.RepoRoot, "transcript.jsonl");
    await File.WriteAllTextAsync(transcriptPath, """
      {"role":"assistant","content":"Decision: use AddScoped for IPricingService because request state must not leak."}
      {"role":"assistant","content":"Open item: still need to migrate legacy pricing seed data."}
      """);

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m6_001",
        "session_id": "sess_m6_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task checkpoint extraction"
      }
      """);

    var preCompactPayload = $$"""
      {
        "conversation_id": "conv_m6_001",
        "session_id": "sess_m6_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "preCompact",
        "context_usage_percent": 82,
        "context_tokens": 12400,
        "trigger": "near_limit",
        "transcript_path": "{{transcriptPath}}"
      }
      """;

    var first = await ExecuteHookAsync("preCompact", preCompactPayload);
    var claimsAfterFirst = fixture.Scalar("SELECT COUNT(*) FROM claim;");
    var second = await ExecuteHookAsync("preCompact", preCompactPayload);
    var claimsAfterSecond = fixture.Scalar("SELECT COUNT(*) FROM claim;");

    Assert.Equal(0, first.ExitCode);
    Assert.Contains("\"user_message\":\"Waypoint checkpoint:", first.StdOut, StringComparison.Ordinal);
    Assert.Equal(0, second.ExitCode);
    Assert.Equal(claimsAfterFirst, claimsAfterSecond);
  }

  [Fact]
  public async Task Stop_writes_resume_md()
  {
    await using var fixture = await TempRepoFixture.CreateAsync("waypoint-cli-m6-stop-resume");

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m6_002",
        "session_id": "sess_m6_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task generate resume file"
      }
      """);

    var stopResult = await ExecuteHookAsync("stop", $$"""
      {
        "conversation_id": "conv_m6_002",
        "session_id": "sess_m6_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "stop",
        "status": "completed",
        "loop_count": 3
      }
      """);

    Assert.Equal(0, stopResult.ExitCode);
    Assert.Equal("{}\n", stopResult.StdOut);

    var resumePath = Path.Combine(fixture.RepoRoot, ".cursor", "waypoint", "RESUME.md");
    Assert.True(File.Exists(resumePath));
    var resume = await File.ReadAllTextAsync(resumePath);
    Assert.Contains("## Waypoint resume brief", resume, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Stop_spawns_background_index_only_after_cs_edit()
  {
    await using var fixture = await TempRepoFixture.CreateAsync("waypoint-cli-m6-stop-index");
    var launcher = new FakeBackgroundProcessLauncher();

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m6_003",
        "session_id": "sess_m6_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task maybe run background index"
      }
      """);

    var noEditStop = await ExecuteHookAsync("stop", $$"""
      {
        "conversation_id": "conv_m6_003",
        "session_id": "sess_m6_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "stop"
      }
      """, new HookRunnerOptions(BackgroundProcessLauncher: launcher));

    Assert.Equal(0, noEditStop.ExitCode);
    Assert.Equal(0, launcher.LaunchCount);

    fixture.ExecuteNonQuery("""
      INSERT INTO episode(session_id, kind, ts, payload_json, summary)
      VALUES('sess_m6_003', 'edit', '2026-10-07T00:00:00.0000000Z', '{"file":"src/ApplicationCore/Services/OrderService.cs"}', 'edit OrderService.cs');
      """);

    var withEditStop = await ExecuteHookAsync("stop", $$"""
      {
        "conversation_id": "conv_m6_003",
        "session_id": "sess_m6_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "stop"
      }
      """, new HookRunnerOptions(BackgroundProcessLauncher: launcher));

    var secondStopWithoutNewEdit = await ExecuteHookAsync("stop", $$"""
      {
        "conversation_id": "conv_m6_003",
        "session_id": "sess_m6_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "stop"
      }
      """, new HookRunnerOptions(BackgroundProcessLauncher: launcher));

    Assert.Equal(0, withEditStop.ExitCode);
    Assert.Equal(0, secondStopWithoutNewEdit.ExitCode);
    Assert.Equal(1, launcher.LaunchCount);
  }

  [Fact]
  public async Task AfterAgentThought_extracts_claims_with_reduced_confidence()
  {
    await using var fixture = await TempRepoFixture.CreateAsync("waypoint-cli-m6-thought");

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m6_004",
        "session_id": "sess_m6_004",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task thought extraction"
      }
      """);

    var result = await ExecuteHookAsync("afterAgentThought", $$"""
      {
        "conversation_id": "conv_m6_004",
        "session_id": "sess_m6_004",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "afterAgentThought",
        "thought_text": "Decision: use AddScoped for pricing because it must align with scoped DbContext."
      }
      """);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);

    var confidence = fixture.Double("""
      SELECT confidence
      FROM claim
      ORDER BY id DESC
      LIMIT 1;
      """);
    Assert.True(confidence < 0.7, $"Expected reduced thought confidence, got {confidence}.");
  }

  private static async Task<HookExecutionResult> ExecuteHookAsync(string eventName, string payload, HookRunnerOptions? options = null)
  {
    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();

    var exitCode = await HookRunner.RunAsync(
      eventName,
      input,
      output,
      options ?? new HookRunnerOptions());

    return new HookExecutionResult(exitCode, Encoding.UTF8.GetString(output.ToArray()));
  }

  private sealed record HookExecutionResult(int ExitCode, string StdOut);

  private sealed class FakeBackgroundProcessLauncher : IBackgroundProcessLauncher
  {
    public int LaunchCount { get; private set; }

    public void LaunchIndex(string repoRoot)
    {
      _ = repoRoot;
      LaunchCount++;
    }
  }

  private sealed class TempRepoFixture : IAsyncDisposable
  {
    private readonly string _root;
    private SqliteConnection? _connection;

    private TempRepoFixture(string root)
    {
      _root = root;
    }

    public string RepoRoot => _root;

    public static Task<TempRepoFixture> CreateAsync(string prefix)
    {
      var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path.Combine(root, ".git"));
      File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
      WaypointDb.Open(WaypointPaths.ForRepo(root).DbPath).Dispose();
      return Task.FromResult(new TempRepoFixture(root));
    }

    public long Scalar(string sql)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public double Double(string sql)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      return Convert.ToDouble(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void ExecuteNonQuery(string sql)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
    }

    public ValueTask DisposeAsync()
    {
      _connection?.Dispose();
      if (Directory.Exists(_root))
      {
        Directory.Delete(_root, recursive: true);
      }

      return ValueTask.CompletedTask;
    }
  }
}
