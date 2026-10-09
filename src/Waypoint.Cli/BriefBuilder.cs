using Microsoft.Data.Sqlite;
using Waypoint.Core.Brief;

namespace Waypoint.Cli;

internal static class BriefBuilder
{
  public static string? RenderForActiveTask(SqliteConnection connection, string repoRoot, string? branch)
  {
    var renderer = new BriefRenderer(connection);
    return renderer.RenderForActiveTask(repoRoot, branch);
  }

  public static string? RenderForTask(SqliteConnection connection, long taskId)
  {
    var renderer = new BriefRenderer(connection);
    return renderer.RenderForTask(taskId);
  }
}
