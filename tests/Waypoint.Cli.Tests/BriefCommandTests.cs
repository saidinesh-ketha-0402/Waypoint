using System.Text;
using Waypoint.Cli;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class BriefCommandTests
{
  [Fact]
  public async Task Brief_with_active_task_prints_resume_brief()
  {
    var repoRoot = CreateTempRepoRoot();
    try
    {
      await ExecuteHookAsync("beforeSubmitPrompt", $$"""
        {
          "conversation_id": "conv_brief_001",
          "session_id": "sess_brief_001",
          "workspace_roots": ["{{repoRoot}}"],
          "hook_event_name": "beforeSubmitPrompt",
          "prompt": "#task add discount endpoint"
        }
        """);

      var output = await ExecuteBriefAsync(repoRoot, taskId: null);
      Assert.Contains("## Waypoint resume brief — task: add discount endpoint", output, StringComparison.Ordinal);
      Assert.Contains("**Goal.** add discount endpoint", output, StringComparison.Ordinal);
    }
    finally
    {
      DeleteDirectoryIfExists(repoRoot);
    }
  }

  [Fact]
  public async Task Brief_without_active_task_prints_expected_message()
  {
    var repoRoot = CreateTempRepoRoot();
    try
    {
      var output = await ExecuteBriefAsync(repoRoot, taskId: null);
      Assert.Contains("No active Waypoint task for", output, StringComparison.Ordinal);
    }
    finally
    {
      DeleteDirectoryIfExists(repoRoot);
    }
  }

  private static async Task<string> ExecuteBriefAsync(string repoRoot, long? taskId)
  {
    var originalOut = Console.Out;
    using var writer = new StringWriter();
    Console.SetOut(writer);
    try
    {
      var exitCode = await BriefCommand.ExecuteAsync(repoRoot, taskId, CancellationToken.None);
      Assert.Equal(0, exitCode);
      return writer.ToString();
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }

  private static async Task ExecuteHookAsync(string eventName, string payload)
  {
    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();
    var exitCode = await HookRunner.RunAsync(eventName, input, output, new HookRunnerOptions());
    Assert.Equal(0, exitCode);
  }

  private static string CreateTempRepoRoot()
  {
    var repoRoot = Path.Combine(Path.GetTempPath(), "waypoint-brief-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
    File.WriteAllText(Path.Combine(repoRoot, ".git", "HEAD"), "ref: refs/heads/main", Encoding.UTF8);
    return repoRoot;
  }

  private static void DeleteDirectoryIfExists(string repoRoot)
  {
    if (Directory.Exists(repoRoot))
    {
      Directory.Delete(repoRoot, recursive: true);
    }
  }
}
