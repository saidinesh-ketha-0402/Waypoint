using System.Text.RegularExpressions;

namespace Waypoint.Core.Parsing;

public sealed class DotnetOutputParser(string repoRoot)
{
  private static readonly Regex DotnetCommandRegex = new(
    @"(^|[;&|]\s*|\s)dotnet\s+(?<cmd>build|test|run|ef)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex CompilerDiagnosticRegex = new(
    @"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>CS\d{4}):\s+(?<msg>.*?)(\s+\[[^\]]+\])?\s*$",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex VstestSummaryRegex = new(
    @"(Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex FailedTestNameRegex = new(
    @"^\s*Failed\s+(?<name>[^\s\[]+)\s+\[",
    RegexOptions.Compiled);

  private static readonly Regex XunitFailedTestNameRegex = new(
    @"^\s*\[xUnit\.net.*\]\s+(?<name>[^\s\[]+)\s+\[FAIL\]",
    RegexOptions.Compiled);

  private static readonly Regex ExceptionRegex = new(
    @"(?<type>[A-Z]\w*(\.\w+)*Exception):\s+(?<msg>.+)",
    RegexOptions.Compiled);

  private static readonly Regex EfMigrationAddedRegex = new(
    @"dotnet\s+ef\s+migrations\s+add\s+(?<id>[A-Za-z0-9_]+)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private readonly string _repoRoot = string.IsNullOrWhiteSpace(repoRoot)
    ? string.Empty
    : Path.GetFullPath(repoRoot);

  public ShellResult? Parse(string command, string output, int? exitCode = null)
  {
    var commandMatch = DotnetCommandRegex.Match(command ?? string.Empty);
    if (!commandMatch.Success)
    {
      return null;
    }

    var dotnetCommand = commandMatch.Groups["cmd"].Value.ToLowerInvariant();
    var diagnostics = new List<CompilerDiagnostic>();
    var diagnosticsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var failedTestNames = new HashSet<string>(StringComparer.Ordinal);
    DotnetExceptionInfo? firstException = null;
    string? diError = null;
    string? buildStatus = null;
    int testsPassed = 0;
    int testsFailed = 0;
    int testsSkipped = 0;
    string? efMigrationAdded = TryReadEfMigrationId(command);
    string? efError = null;

    var lines = (output ?? string.Empty)
      .Replace("\r\n", "\n", StringComparison.Ordinal)
      .Replace('\r', '\n')
      .Split('\n');

    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      if (string.IsNullOrWhiteSpace(line))
      {
        continue;
      }

      var diagnosticMatch = CompilerDiagnosticRegex.Match(line);
      if (diagnosticMatch.Success)
      {
        var filePath = NormalizeToRepoRelative(diagnosticMatch.Groups["file"].Value.Trim());
        var lineNumber = ReadInt(diagnosticMatch.Groups["line"].Value);
        var columnNumber = ReadInt(diagnosticMatch.Groups["col"].Value);
        var severity = diagnosticMatch.Groups["sev"].Value.ToLowerInvariant();
        var code = diagnosticMatch.Groups["code"].Value.ToUpperInvariant();
        var message = diagnosticMatch.Groups["msg"].Value.Trim();
        var dedupeKey = $"{filePath}:{lineNumber}:{code}";

        if (diagnosticsSeen.Add(dedupeKey))
        {
          diagnostics.Add(new CompilerDiagnostic(filePath, lineNumber, columnNumber, severity, code, message));
        }
      }

      if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
      {
        buildStatus = "ok";
      }
      else if (line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(line, @"build failed with \d+ error", RegexOptions.IgnoreCase))
      {
        buildStatus = "failed";
      }

      var vstestSummaryMatch = VstestSummaryRegex.Match(line);
      if (vstestSummaryMatch.Success)
      {
        testsFailed += ReadInt(vstestSummaryMatch.Groups["failed"].Value);
        testsPassed += ReadInt(vstestSummaryMatch.Groups["passed"].Value);
        testsSkipped += ReadInt(vstestSummaryMatch.Groups["skipped"].Value);
      }

      var failedTestMatch = FailedTestNameRegex.Match(line);
      if (failedTestMatch.Success)
      {
        var testName = failedTestMatch.Groups["name"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(testName))
        {
          failedTestNames.Add(testName);
        }
      }
      else
      {
        var xunitFailedTestMatch = XunitFailedTestNameRegex.Match(line);
        if (xunitFailedTestMatch.Success)
        {
          var testName = xunitFailedTestMatch.Groups["name"].Value.Trim();
          if (!string.IsNullOrWhiteSpace(testName))
          {
            failedTestNames.Add(testName);
          }
        }
      }

      var exceptionMatch = ExceptionRegex.Match(line);
      if (exceptionMatch.Success)
      {
        var candidate = new DotnetExceptionInfo(
          exceptionMatch.Groups["type"].Value.Trim(),
          TrimToMax(exceptionMatch.Groups["msg"].Value.Trim(), 200));

        if (firstException is null)
        {
          firstException = candidate;
        }
        else if (string.Equals(firstException.Type, "System.AggregateException", StringComparison.Ordinal)
                 && !string.Equals(candidate.Type, "System.AggregateException", StringComparison.Ordinal))
        {
          // Prefer the first inner exception over AggregateException for actionable failures.
          firstException = candidate;
        }
      }

      if (diError is null)
      {
        diError = TryReadDiSignal(line);
      }

      if (efError is null)
      {
        if (line.Contains("has already been applied", StringComparison.OrdinalIgnoreCase))
        {
          efError = line.Trim();
        }
        else if (line.Contains("Pending model changes", StringComparison.OrdinalIgnoreCase))
        {
          efError = line.Trim();
        }
        else if (line.Contains("doesn't reference Microsoft.EntityFrameworkCore.Design", StringComparison.OrdinalIgnoreCase))
        {
          efError = line.Trim();
        }
      }

      if (line.Contains("Test run summary:", StringComparison.OrdinalIgnoreCase))
      {
        ParseMtpSummary(lines, i + 1, ref testsPassed, ref testsFailed, ref testsSkipped);
      }
    }

    var tests = testsPassed > 0 || testsFailed > 0 || testsSkipped > 0 || failedTestNames.Count > 0
      ? new DotnetTestsSummary(testsPassed, testsFailed, testsSkipped, failedTestNames.Order(StringComparer.Ordinal).ToArray())
      : null;

    var ef = efMigrationAdded is not null || efError is not null
      ? new DotnetEfResult(efMigrationAdded, efError)
      : null;

    var isFailed = (exitCode ?? 0) != 0
      || string.Equals(buildStatus, "failed", StringComparison.Ordinal)
      || (tests?.Failed ?? 0) > 0
      || firstException is not null
      || diError is not null;

    var inferredExitCode = exitCode ?? (isFailed ? 1 : 0);

    return new ShellResult(
      DotnetCommand: dotnetCommand,
      Build: buildStatus,
      Failed: isFailed,
      ExitCode: inferredExitCode,
      Errors: diagnostics,
      Tests: tests,
      Exception: firstException,
      DiError: diError,
      Ef: ef);
  }

  private static string? TryReadEfMigrationId(string? command)
  {
    var match = EfMigrationAddedRegex.Match(command ?? string.Empty);
    return match.Success ? match.Groups["id"].Value : null;
  }

  private static string? TryReadDiSignal(string line)
  {
    var trimmed = line.Trim();
    if (trimmed.Contains("Cannot consume scoped service", StringComparison.OrdinalIgnoreCase)
        || trimmed.Contains("Unable to resolve service for type", StringComparison.OrdinalIgnoreCase)
        || trimmed.Contains("No service for type", StringComparison.OrdinalIgnoreCase)
        || trimmed.Contains("NoServiceRegistered", StringComparison.OrdinalIgnoreCase)
        || trimmed.Contains("ScopedInSingletonException", StringComparison.OrdinalIgnoreCase)
        || trimmed.Contains("A second operation was started on this context", StringComparison.OrdinalIgnoreCase))
    {
      return trimmed;
    }

    return null;
  }

  private static void ParseMtpSummary(string[] lines, int startIndex, ref int passed, ref int failed, ref int skipped)
  {
    const int maxLookahead = 8;
    var end = Math.Min(lines.Length, startIndex + maxLookahead);

    for (var i = startIndex; i < end; i++)
    {
      var line = lines[i].Trim();
      if (line.Length == 0)
      {
        continue;
      }

      if (line.StartsWith("total:", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (line.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
      {
        failed += ReadInt(line["failed:".Length..].Trim());
      }
      else if (line.StartsWith("succeeded:", StringComparison.OrdinalIgnoreCase))
      {
        passed += ReadInt(line["succeeded:".Length..].Trim());
      }
      else if (line.StartsWith("skipped:", StringComparison.OrdinalIgnoreCase))
      {
        skipped += ReadInt(line["skipped:".Length..].Trim());
      }
      else if (line.StartsWith("passed:", StringComparison.OrdinalIgnoreCase))
      {
        passed += ReadInt(line["passed:".Length..].Trim());
      }
    }
  }

  private string NormalizeToRepoRelative(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return path;
    }

    var normalized = path.Replace('\\', '/');
    if (_repoRoot.Length == 0)
    {
      return normalized;
    }

    if (Path.IsPathRooted(path))
    {
      var full = Path.GetFullPath(path).Replace('\\', '/');
      var root = _repoRoot.Replace('\\', '/').TrimEnd('/');
      if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
      {
        return full[(root.Length + 1)..];
      }

      return full;
    }

    return normalized;
  }

  private static int ReadInt(string value) =>
    int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
      ? parsed
      : 0;

  private static string TrimToMax(string value, int maxChars) =>
    value.Length <= maxChars ? value : value[..maxChars];
}

public sealed record ShellResult(
  string DotnetCommand,
  string? Build,
  bool Failed,
  int ExitCode,
  IReadOnlyList<CompilerDiagnostic> Errors,
  DotnetTestsSummary? Tests,
  DotnetExceptionInfo? Exception,
  string? DiError,
  DotnetEfResult? Ef);

public sealed record CompilerDiagnostic(
  string File,
  int Line,
  int Column,
  string Severity,
  string Code,
  string Message);

public sealed record DotnetTestsSummary(
  int Passed,
  int Failed,
  int Skipped,
  IReadOnlyList<string> FailedNames);

public sealed record DotnetExceptionInfo(string Type, string Message);

public sealed record DotnetEfResult(string? MigrationAdded, string? Error);
