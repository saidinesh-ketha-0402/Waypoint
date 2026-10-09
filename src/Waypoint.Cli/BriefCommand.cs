using Waypoint.Core.Paths;
using Waypoint.Core.Storage;

namespace Waypoint.Cli;

public static class BriefCommand
{
  public static Task<int> ExecuteAsync(string? repoRootOverride, long? taskId, CancellationToken cancellationToken)
  {
    _ = cancellationToken;
    var repoRoot = ResolveRepoRoot(repoRootOverride);
    var branch = GitInfo.CurrentBranch(repoRoot);
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var brief = taskId.HasValue
      ? BriefBuilder.RenderForTask(connection, taskId.Value)
      : BriefBuilder.RenderForActiveTask(connection, repoRoot, branch);

    if (string.IsNullOrWhiteSpace(brief))
    {
      Console.Out.WriteLine($"No active Waypoint task for {repoRoot}@{branch ?? "unknown"}");
      return Task.FromResult(0);
    }

    Console.Out.WriteLine(brief);
    return Task.FromResult(0);
  }

  private static string ResolveRepoRoot(string? repoRootOverride)
  {
    if (!string.IsNullOrWhiteSpace(repoRootOverride))
    {
      return Path.GetFullPath(repoRootOverride);
    }

    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null)
    {
      var gitPath = Path.Combine(current.FullName, ".git");
      if (Directory.Exists(gitPath) || File.Exists(gitPath))
      {
        return current.FullName;
      }

      current = current.Parent;
    }

    return Environment.CurrentDirectory;
  }
}
