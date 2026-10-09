namespace Waypoint.Core.Paths;

public static class GitInfo
{
  public static string CurrentBranch(string repoRoot)
  {
    var paths = WaypointPaths.ForRepo(repoRoot);
    if (!paths.IsGitBacked)
    {
      return "no-git";
    }

    var headPath = Path.Combine(paths.StoreRoot, "..", "HEAD");
    if (!File.Exists(headPath))
    {
      return "unknown";
    }

    var head = File.ReadAllText(headPath).Trim();
    const string branchPrefix = "ref: refs/heads/";
    if (head.StartsWith(branchPrefix, StringComparison.Ordinal))
    {
      return head[branchPrefix.Length..];
    }

    if (head.Length >= 7)
    {
      return $"detached@{head[..7]}";
    }

    return "unknown";
  }
}
