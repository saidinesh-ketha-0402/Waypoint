using Microsoft.Data.Sqlite;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Viz;

namespace Waypoint.Cli;

public static class ExportCommand
{
  public static Task<int> ExecuteAsync(
    string? repoRootOverride,
    long? taskId,
    string? outputDirectory,
    ExportFormat format,
    bool includeUntouchedEndpoints,
    CancellationToken cancellationToken)
  {
    _ = cancellationToken;

    var repoRoot = ResolveRepoRoot(repoRootOverride);
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    if (!File.Exists(dbPath))
    {
      Console.Error.WriteLine("Waypoint graph database was not found. Run `waypoint index` first.");
      return Task.FromResult(1);
    }

    using var connection = WaypointDb.Open(dbPath);
    var resolvedTaskId = taskId ?? ResolveActiveTaskId(connection, repoRoot);
    if (!resolvedTaskId.HasValue)
    {
      Console.Error.WriteLine("No active task found. Pass --task <id> or start a task in this repo.");
      return Task.FromResult(1);
    }

    var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
      ? Path.Combine(WaypointPaths.ForRepo(repoRoot).StoreRoot, "viz")
      : Path.GetFullPath(outputDirectory!);
    var exporter = new TaskGraphExporter(connection);
    var result = exporter.Export(new ExportRequest(
      resolvedTaskId.Value,
      outputRoot,
      format,
      includeUntouchedEndpoints));

    Console.Out.WriteLine(result.MainOutputPath);
    return Task.FromResult(0);
  }

  private static long? ResolveActiveTaskId(SqliteConnection connection, string repoRoot)
  {
    var branch = GitInfo.CurrentBranch(repoRoot);
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM task
      WHERE repo_root = $repo_root
        AND (branch = $branch OR (branch IS NULL AND $branch IS NULL))
        AND status = 'active'
      ORDER BY updated_at DESC, id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$repo_root", repoRoot);
    command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
    var result = command.ExecuteScalar();
    return result is long id ? id : null;
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
