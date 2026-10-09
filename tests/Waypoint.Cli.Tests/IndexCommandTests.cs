using System.Diagnostics;
using System.Globalization;
using Waypoint.Cli;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class IndexCommandTests
{
  [Fact]
  public async Task Index_with_valid_repo_prints_report_and_writes_graph_db()
  {
    var repoRoot = CopyDemoShopFixture();
    EnsureGitDirectory(repoRoot);
    var cliAssemblyPath = typeof(HookPingCommand).Assembly.Location;

    var process = await RunCliAsync(
      cliAssemblyPath,
      $"index --repo \"{repoRoot}\" --solution \"{Path.Combine(repoRoot, "DemoShop.slnx")}\" --project Shop.Core");

    Assert.Equal(0, process.ExitCode);
    Assert.Contains("mode=msbuild", process.StdOut, StringComparison.Ordinal);
    Assert.Contains("projects=", process.StdOut, StringComparison.Ordinal);
    Assert.Contains("edges:", process.StdOut, StringComparison.Ordinal);
    Assert.Equal(string.Empty, process.StdErr);

    var dbPath = Path.Combine(repoRoot, ".git", "waypoint", "graph.db");
    Assert.True(File.Exists(dbPath));
  }

  [Fact]
  public async Task Index_with_missing_solution_exits_1()
  {
    var repoRoot = CopyDemoShopFixture();
    var cliAssemblyPath = typeof(HookPingCommand).Assembly.Location;

    var process = await RunCliAsync(
      cliAssemblyPath,
      $"index --repo \"{repoRoot}\" --solution \"{Path.Combine(repoRoot, "missing.slnx")}\"");

    Assert.Equal(1, process.ExitCode);
    Assert.Contains("does not exist", process.StdErr, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Index_background_exits_zero_when_live_lock_exists()
  {
    var repoRoot = CreateTempRepoRoot();
    var lockDirectory = Path.Combine(repoRoot, ".git", "waypoint");
    Directory.CreateDirectory(lockDirectory);
    var lockPath = Path.Combine(lockDirectory, "index.lock");
    var lockBody = FormattableString.Invariant(
      $"pid={Environment.ProcessId}\nstarted_at={DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\n");
    File.WriteAllText(lockPath, lockBody);

    var cliAssemblyPath = typeof(HookPingCommand).Assembly.Location;
    var process = await RunCliAsync(
      cliAssemblyPath,
      $"index --background --quiet --repo \"{repoRoot}\" --solution \"{Path.Combine(repoRoot, "missing.slnx")}\"");

    Assert.Equal(0, process.ExitCode);
    Assert.Equal(string.Empty, process.StdOut);
    Assert.Equal(string.Empty, process.StdErr);
    Assert.True(File.Exists(lockPath));
  }

  private static async Task<CliResult> RunCliAsync(string cliAssemblyPath, string arguments)
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "dotnet",
        Arguments = $"\"{cliAssemblyPath}\" {arguments}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      }
    };

    process.Start();
    var stdOutTask = process.StandardOutput.ReadToEndAsync();
    var stdErrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return new CliResult(process.ExitCode, await stdOutTask, await stdErrTask);
  }

  private static string CopyDemoShopFixture()
  {
    var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "DemoShop"));
    var destination = CreateTempRepoRoot();
    CopyDirectory(fixtureRoot, destination);
    return destination;
  }

  private static void EnsureGitDirectory(string repoRoot)
  {
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
  }

  private static string CreateTempRepoRoot()
  {
    var repoRoot = Path.Combine(Path.GetTempPath(), $"waypoint-index-{Guid.NewGuid():N}");
    Directory.CreateDirectory(repoRoot);
    Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
    return repoRoot;
  }

  private static void CopyDirectory(string source, string destination)
  {
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
    {
      var relative = Path.GetRelativePath(source, directory);
      Directory.CreateDirectory(Path.Combine(destination, relative));
    }

    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
      var relative = Path.GetRelativePath(source, file);
      var targetFile = Path.Combine(destination, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
      File.Copy(file, targetFile, overwrite: true);
    }
  }

  private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}
