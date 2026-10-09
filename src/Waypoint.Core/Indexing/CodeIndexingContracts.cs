namespace Waypoint.Core.Indexing;

public enum IndexMode
{
  MsBuild,
  Compilation,
  Syntax,
  Auto
}

public sealed record IndexOptions(string RepoRoot)
{
  public string? SolutionPath { get; init; }
  public IReadOnlyList<string> Projects { get; init; } = [];
  public IndexMode Mode { get; init; } = IndexMode.MsBuild;
  public bool Background { get; init; }
  public bool Force { get; init; }
}

public sealed record WorkspaceDiagnostic(string Kind, string Message);

public sealed record IndexReport(
  IndexMode Mode,
  int Projects,
  int FilesSeen,
  int FilesSkipped,
  int Nodes,
  IReadOnlyDictionary<string, int> EdgesByKind,
  IReadOnlyList<WorkspaceDiagnostic> Diagnostics,
  TimeSpan Elapsed);

public interface ICodeIndexer
{
  Task<IndexReport> IndexAsync(IndexOptions options, CancellationToken cancellationToken = default);
}

public sealed record FileRefreshRequest(string RepoRoot, string RelativePath, string FileText);

public sealed record RefreshedNodeSpan(
  long NodeId,
  string Kind,
  string Name,
  string QualifiedName,
  string FilePath,
  int StartLine,
  int EndLine);

public sealed class FileRefreshResult(IReadOnlyList<RefreshedNodeSpan> nodesInFile)
{
  private readonly IReadOnlyList<RefreshedNodeSpan> _nodesInFile = nodesInFile ?? throw new ArgumentNullException(nameof(nodesInFile));

  public IReadOnlyList<RefreshedNodeSpan> NodesInRange(int startLine, int endLine)
  {
    if (startLine <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(startLine), "startLine must be >= 1.");
    }

    if (endLine < startLine)
    {
      throw new ArgumentOutOfRangeException(nameof(endLine), "endLine must be >= startLine.");
    }

    return _nodesInFile
      .Where(node => node.EndLine >= startLine && node.StartLine <= endLine)
      .OrderBy(node => node.StartLine)
      .ThenBy(node => node.EndLine)
      .ToArray();
  }
}

public interface ISyntaxRefresher
{
  FileRefreshResult Refresh(FileRefreshRequest request, CancellationToken cancellationToken = default);
}
