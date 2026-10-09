namespace Waypoint.Core.Tests;

internal static class TestRepo
{
  public static string CreateTempDirectory()
  {
    var path = Path.Combine(Path.GetTempPath(), "waypoint-core-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  public static string CreateGitRepo()
  {
    var root = CreateTempDirectory();
    var gitDir = Path.Combine(root, ".git");
    Directory.CreateDirectory(gitDir);
    File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main");
    return root;
  }
}
