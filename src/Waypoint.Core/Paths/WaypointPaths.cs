namespace Waypoint.Core.Paths;

public sealed record WaypointPaths(
  string RepoRoot,
  string StoreRoot,
  string DbPath,
  string LogPath,
  string ResumePath,
  string IndexLockPath,
  bool IsGitBacked)
{
  public static WaypointPaths ForRepo(string repoRoot)
  {
    if (string.IsNullOrWhiteSpace(repoRoot))
    {
      throw new ArgumentException("Repo root is required.", nameof(repoRoot));
    }

    var fullRepoRoot = Path.GetFullPath(repoRoot);
    var gitDirectory = ResolveGitDirectory(fullRepoRoot);
    var isGitBacked = gitDirectory is not null;
    var storeRoot = isGitBacked
      ? Path.Combine(gitDirectory!, "waypoint")
      : Path.Combine(fullRepoRoot, ".waypoint");

    return new WaypointPaths(
      fullRepoRoot,
      storeRoot,
      Path.Combine(storeRoot, "graph.db"),
      Path.Combine(storeRoot, "hooks.log"),
      Path.Combine(fullRepoRoot, ".cursor", "waypoint", "RESUME.md"),
      Path.Combine(storeRoot, "index.lock"),
      isGitBacked);
  }

  private static string? ResolveGitDirectory(string repoRoot)
  {
    var gitPath = Path.Combine(repoRoot, ".git");
    if (Directory.Exists(gitPath))
    {
      return Path.GetFullPath(gitPath);
    }

    if (!File.Exists(gitPath))
    {
      return null;
    }

    var content = File.ReadAllText(gitPath).Trim();
    const string prefix = "gitdir:";
    if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    var gitDirValue = content[prefix.Length..].Trim();
    if (string.IsNullOrWhiteSpace(gitDirValue))
    {
      return null;
    }

    return Path.GetFullPath(
      Path.IsPathRooted(gitDirValue)
        ? gitDirValue
        : Path.Combine(repoRoot, gitDirValue));
  }
}
