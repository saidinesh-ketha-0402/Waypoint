using System.Diagnostics;
using System.Globalization;
using System.Text;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Roslyn.Semantic;

namespace Waypoint.Cli;

internal static class IndexCommand
{
  private static readonly TimeSpan ActiveLockMaxAge = TimeSpan.FromMinutes(10);

  public static async Task<int> ExecuteAsync(
    string? repoRootOverride,
    string? solutionPath,
    IReadOnlyList<string> projects,
    IndexMode mode,
    bool force,
    bool background,
    bool quiet,
    CancellationToken cancellationToken)
  {
    try
    {
      var repoRoot = ResolveRepoRoot(repoRootOverride);
      var paths = WaypointPaths.ForRepo(repoRoot);
      var options = new IndexOptions(repoRoot)
      {
        SolutionPath = solutionPath,
        Projects = projects,
        Mode = mode,
        Force = force,
        Background = background
      };

      LockLease? lease = null;
      try
      {
        if (background)
        {
          lease = TryAcquireBackgroundLock(paths.IndexLockPath);
          if (lease is null)
          {
            return 0;
          }
        }

        var indexer = new SemanticIndexer();
        var report = await indexer.IndexAsync(options, cancellationToken);

        if (!quiet)
        {
          Console.Out.WriteLine(FormatReport(report));
          foreach (var diagnostic in report.Diagnostics)
          {
            Console.Out.WriteLine($"diag[{diagnostic.Kind}] {diagnostic.Message}");
          }
        }

        return 0;
      }
      finally
      {
        lease?.Dispose();
      }
    }
    catch (FileNotFoundException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
    catch (InvalidOperationException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
    catch (NotSupportedException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
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

  private static string FormatReport(IndexReport report)
  {
    var edges = report.EdgesByKind
      .OrderBy(pair => pair.Key, StringComparer.Ordinal)
      .Select(pair => $"{pair.Key}={pair.Value}");
    var edgeSummary = string.Join(' ', edges);
    return FormattableString.Invariant(
      $"mode={report.Mode.ToString().ToLowerInvariant()} projects={report.Projects} files_seen={report.FilesSeen} files_skipped={report.FilesSkipped} nodes={report.Nodes} edges: {edgeSummary} diagnostics={report.Diagnostics.Count} elapsed_ms={(int)report.Elapsed.TotalMilliseconds}");
  }

  private static LockLease? TryAcquireBackgroundLock(string lockPath)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");
    if (File.Exists(lockPath))
    {
      var existing = TryReadLock(lockPath);
      if (existing is not null &&
          DateTimeOffset.UtcNow - existing.StartedAt <= ActiveLockMaxAge &&
          IsProcessAlive(existing.Pid))
      {
        return null;
      }
    }

    var lockInfo = new LockInfo(Environment.ProcessId, DateTimeOffset.UtcNow);
    var content = FormattableString.Invariant($"pid={lockInfo.Pid}\nstarted_at={lockInfo.StartedAt:O}\n");
    File.WriteAllText(lockPath, content, Encoding.UTF8);
    return new LockLease(lockPath);
  }

  private static LockInfo? TryReadLock(string lockPath)
  {
    try
    {
      var lines = File.ReadAllLines(lockPath);
      int? pid = null;
      DateTimeOffset? startedAt = null;
      foreach (var line in lines)
      {
        if (line.StartsWith("pid=", StringComparison.Ordinal))
        {
          if (int.TryParse(line["pid=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid))
          {
            pid = parsedPid;
          }

          continue;
        }

        if (line.StartsWith("started_at=", StringComparison.Ordinal))
        {
          if (DateTimeOffset.TryParse(line["started_at=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedStartedAt))
          {
            startedAt = parsedStartedAt;
          }
        }
      }

      return pid is not null && startedAt is not null
        ? new LockInfo(pid.Value, startedAt.Value)
        : null;
    }
    catch
    {
      return null;
    }
  }

  private static bool IsProcessAlive(int pid)
  {
    if (pid <= 0)
    {
      return false;
    }

    try
    {
      var process = Process.GetProcessById(pid);
      return !process.HasExited;
    }
    catch
    {
      return false;
    }
  }

  private sealed record LockInfo(int Pid, DateTimeOffset StartedAt);

  private sealed class LockLease(string lockPath) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0)
      {
        return;
      }

      try
      {
        if (File.Exists(lockPath))
        {
          File.Delete(lockPath);
        }
      }
      catch
      {
        // Best-effort cleanup.
      }
    }
  }
}
