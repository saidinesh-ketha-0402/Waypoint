using Waypoint.Core.Paths;

namespace Waypoint.Mcp;

public sealed class WaypointContext
{
  public WaypointContext(string repoRoot)
  {
    if (string.IsNullOrWhiteSpace(repoRoot))
    {
      throw new ArgumentException("Repo root is required.", nameof(repoRoot));
    }

    RepoRoot = Path.GetFullPath(repoRoot);
    Paths = WaypointPaths.ForRepo(RepoRoot);
  }

  public string RepoRoot { get; }

  public WaypointPaths Paths { get; }

  public string CurrentBranch() => GitInfo.CurrentBranch(RepoRoot);
}
