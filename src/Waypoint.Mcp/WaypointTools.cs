using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Waypoint.Core.Brief;
using Waypoint.Core.Extraction;
using Waypoint.Core.Logging;
using Waypoint.Core.Queries;
using Waypoint.Core.Storage;

namespace Waypoint.Mcp;

[McpServerToolType]
public sealed class WaypointTools(WaypointContext context, ILogger<WaypointTools> logger)
{
  private const int OutputTokenBudget = 800;
  private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
  {
    ClaimKinds.Decision,
    ClaimKinds.Constraint,
    ClaimKinds.AttemptFailed,
    ClaimKinds.OpenItem
  };

  private readonly WaypointContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private readonly ILogger<WaypointTools> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly ITokenCounter _tokenCounter = new CharsDiv4TokenCounter();
  private readonly MarkdownFormatter _markdownFormatter = new();

  [McpServerTool(Name = "resume"), Description("Get the Waypoint resume brief for the current task: goal, what was done and why, decisions and constraints still in force, approaches that failed (do not retry), ordered open items, hot files. Call this first in a new chat if no 'Waypoint resume brief' is already in context.")]
  public string Resume(long? task_id = null) =>
    RunTool(
      "resume",
      () =>
      {
        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var branch = _context.CurrentBranch();
          var taskId = ResolveTaskId(connection, task_id, branch);
          if (!taskId.HasValue)
          {
            return NoActiveTaskMessage(branch);
          }

          var renderer = new BriefRenderer(connection);
          var brief = renderer.RenderForTask(taskId.Value);
          if (string.IsNullOrWhiteSpace(brief))
          {
            return NoActiveTaskMessage(branch);
          }

          var sessionId = ResolveSessionId(connection, taskId.Value);
          if (!string.IsNullOrWhiteSpace(sessionId))
          {
            MarkBriefInjectedViaMcp(connection, sessionId!);
          }

          return brief;
        }
      });

  [McpServerTool(Name = "why"), Description("Explain why a type, member or file is the way it is: decisions, constraints and failed attempts recorded about it, with the session and evidence. Accepts `Type.Member`, `IFoo`, a Roslyn doc-ID, or a repo path.")]
  public string Why(string symbol_or_path) =>
    RunTool(
      "why",
      () =>
      {
        if (string.IsNullOrWhiteSpace(symbol_or_path))
        {
          return "symbol_or_path is required.";
        }

        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var resolver = new SymbolResolver(connection);
          var candidates = resolver.Resolve(symbol_or_path.Trim(), maxCandidates: 5);
          if (candidates.Count == 0)
          {
            return $"No symbol or file matched `{symbol_or_path}`.";
          }

          if (candidates.Count > 1)
          {
            var ambiguous = new StringBuilder();
            ambiguous.AppendLine($"### why: ambiguous symbol `{symbol_or_path}`");
            ambiguous.AppendLine("More than one match was found; retry with a more specific symbol or doc-id.");
            foreach (var candidate in candidates)
            {
              ambiguous.AppendLine($"- `{candidate.QualifiedName}` ({candidate.Kind})");
            }

            return ambiguous.ToString().TrimEnd();
          }

          var node = candidates[0];
          var query = new WhyQuery(connection);
          var entries = query.Run(node.NodeId);
          var ordinalsByTask = BuildSessionOrdinalsByTask(connection, entries.Select(entry => entry.TaskId).Distinct());
          return _markdownFormatter.RenderWhy(node.QualifiedName, entries, ordinalsByTask);
        }
      });

  [McpServerTool(Name = "impact"), Description("Show what depends on a C# symbol: implementations and DI registrations (with lifetime), classes that inject it, callers/callees, HTTP endpoints (verb + route) that reach it, and tests that cover it. Call before changing an interface, a DI registration, or an EF model. ★ marks things already touched in this task.")]
  public string Impact(string symbol, int depth = 2, bool through_di = true) =>
    RunTool(
      "impact",
      () =>
      {
        if (string.IsNullOrWhiteSpace(symbol))
        {
          return "symbol is required.";
        }

        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var resolver = new SymbolResolver(connection);
          var candidates = resolver.Resolve(symbol.Trim(), maxCandidates: 5);
          if (candidates.Count == 0)
          {
            return $"No symbol or file matched `{symbol}`.";
          }

          if (candidates.Count > 1)
          {
            var ambiguous = new StringBuilder();
            ambiguous.AppendLine($"### impact: ambiguous symbol `{symbol}`");
            ambiguous.AppendLine("More than one match was found; retry with a more specific symbol or doc-id.");
            foreach (var candidate in candidates)
            {
              ambiguous.AppendLine($"- `{candidate.QualifiedName}` ({candidate.Kind})");
            }

            return ambiguous.ToString().TrimEnd();
          }

          var branch = _context.CurrentBranch();
          var taskId = ResolveTaskId(connection, null, branch);
          var query = new ImpactQuery(connection);
          var result = query.Run(candidates[0].NodeId, depth: Math.Clamp(depth, 1, 4), throughDi: through_di, taskId: taskId);
          return _markdownFormatter.RenderImpact(candidates[0].Name, result);
        }
      });

  [McpServerTool(Name = "what_changed"), Description("List everything changed in this task so far, grouped by project, with the decision behind each change, session tags, and EF migrations added.")]
  public string WhatChanged(long? task_id = null) =>
    RunTool(
      "what_changed",
      () =>
      {
        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var branch = _context.CurrentBranch();
          var taskId = ResolveTaskId(connection, task_id, branch);
          if (!taskId.HasValue)
          {
            return NoActiveTaskMessage(branch);
          }

          var query = new WhatChangedQuery(connection);
          var result = query.Run(taskId.Value);
          var ordinals = BuildSessionOrdinals(connection, taskId.Value);
          return _markdownFormatter.RenderWhatChanged(result, ordinals);
        }
      });

  [McpServerTool(Name = "open_items"), Description("List the task's open items in priority order. Pass resolve_id to mark one as done. Call before declaring the task complete.")]
  public string OpenItems(long? task_id = null, long? resolve_id = null) =>
    RunTool(
      "open_items",
      () =>
      {
        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var branch = _context.CurrentBranch();
          var taskId = ResolveTaskId(connection, task_id, branch);
          if (!taskId.HasValue)
          {
            return NoActiveTaskMessage(branch);
          }

          var query = new OpenItemsQuery(connection);
          var result = query.Run(taskId.Value, resolve_id);
          return _markdownFormatter.RenderOpenItems(result);
        }
      });

  [McpServerTool(Name = "record_decision"), Description("Record one sentence of task memory explicitly: a decision (e.g. a DI lifetime chosen and why), a constraint discovered, an approach that failed, or an open item. Name the type/member in `about`.")]
  public string RecordDecision(string text, string? about = null, string kind = ClaimKinds.Decision) =>
    RunTool(
      "record_decision",
      () =>
      {
        if (string.IsNullOrWhiteSpace(text))
        {
          return "text is required.";
        }

        if (!AllowedKinds.Contains(kind))
        {
          return $"kind must be one of: {ClaimKinds.Decision}, {ClaimKinds.Constraint}, {ClaimKinds.AttemptFailed}, {ClaimKinds.OpenItem}.";
        }

        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var branch = _context.CurrentBranch();
          var taskId = ResolveTaskId(connection, null, branch);
          if (!taskId.HasValue)
          {
            return NoActiveTaskMessage(branch);
          }

          var resolver = new SymbolResolver(connection);
          var aboutCandidate = string.IsNullOrWhiteSpace(about) ? null : resolver.Resolve(about.Trim(), maxCandidates: 1).FirstOrDefault();
          var aboutNodeId = aboutCandidate?.NodeId;
          var traceStore = new TraceStore(connection);
          var sessionId = ResolveSessionId(connection, taskId.Value);
          if (string.IsNullOrWhiteSpace(sessionId))
          {
            sessionId = "mcp-" + Guid.NewGuid().ToString("N");
            traceStore.SetSessionTask(sessionId, taskId.Value);
          }

          var writer = new ClaimWriter(connection);
          writer.WriteClaims(
            taskId.Value,
            sessionId!,
            null,
            [
              new ExtractedClaim(kind, text.Trim(), 0.95, aboutNodeId, "explicit")
            ]);

          var claimId = FindLatestClaimId(connection, taskId.Value, sessionId!, kind, text.Trim()) ?? 0;
          if (aboutCandidate is null && !string.IsNullOrWhiteSpace(about))
          {
            return $"Recorded {kind} #{claimId} about unresolved `{about}` (saved without symbol link).";
          }

          var aboutText = aboutCandidate is null ? "task" : aboutCandidate.QualifiedName;
          return $"Recorded {kind} #{claimId} about {aboutText}.";
        }
      });

  [McpServerTool(Name = "search"), Description("Full-text search over everything Waypoint recorded for this repo (prompts, tool results, edits, agent responses, decisions). Use for 'did we already try X?'.")]
  public string Search(string query, string? kind = null) =>
    RunTool(
      "search",
      () =>
      {
        if (string.IsNullOrWhiteSpace(query))
        {
          return "query is required.";
        }

        if (!TryOpenConnection(out var connection, out var noDataMessage))
        {
          return noDataMessage;
        }

        using (connection)
        {
          var searchQuery = new SearchQuery(connection);
          var hits = searchQuery.Run(query.Trim(), string.IsNullOrWhiteSpace(kind) ? null : kind!.Trim());
          var taskIds = hits.Where(hit => hit.TaskId.HasValue).Select(hit => hit.TaskId!.Value).Distinct();
          var ordinalsByTask = BuildSessionOrdinalsByTask(connection, taskIds);
          return _markdownFormatter.RenderSearch(query.Trim(), hits, ordinalsByTask);
        }
      });

  private string RunTool(string toolName, Func<string> body)
  {
    try
    {
      return FitTokenBudget(body());
    }
    catch (Exception exception)
    {
      _logger.LogError(exception, "MCP tool {ToolName} failed.", toolName);
      HookLog.Append(_context.Paths.LogPath, $"mcp {toolName} error", new
      {
        error = exception.GetType().Name,
        message = exception.Message
      });
      return FitTokenBudget($"Waypoint error: {ShortMessage(exception.Message)}");
    }
  }

  private string FitTokenBudget(string text)
  {
    var normalized = (text ?? string.Empty).TrimEnd();
    if (_tokenCounter.CountTokens(normalized) <= OutputTokenBudget)
    {
      return normalized;
    }

    var lines = normalized.Split('\n');
    var keptLines = new List<string>(lines.Length);
    for (var i = 0; i < lines.Length; i++)
    {
      keptLines.Add(lines[i]);
      var withRemaining = BuildTruncatedText(keptLines, lines.Length - i - 1);
      if (_tokenCounter.CountTokens(withRemaining) > OutputTokenBudget)
      {
        keptLines.RemoveAt(keptLines.Count - 1);
        break;
      }
    }

    var remainingLines = Math.Max(0, lines.Length - keptLines.Count);
    var truncated = BuildTruncatedText(keptLines, remainingLines);
    while (keptLines.Count > 0 && _tokenCounter.CountTokens(truncated) > OutputTokenBudget)
    {
      keptLines.RemoveAt(keptLines.Count - 1);
      truncated = BuildTruncatedText(keptLines, lines.Length - keptLines.Count);
    }

    if (_tokenCounter.CountTokens(truncated) <= OutputTokenBudget)
    {
      return truncated;
    }

    return "…(truncated)";
  }

  private static string BuildTruncatedText(IReadOnlyList<string> keptLines, int remainingLines)
  {
    var text = keptLines.Count == 0
      ? string.Empty
      : string.Join('\n', keptLines).TrimEnd();
    if (remainingLines <= 0)
    {
      return text;
    }

    var suffix = $"…({remainingLines} more)";
    return string.IsNullOrWhiteSpace(text)
      ? suffix
      : $"{text}\n{suffix}";
  }

  private bool TryOpenConnection(out SqliteConnection connection, out string noDataMessage)
  {
    noDataMessage = "Waypoint has no data for this repo yet — run \"waypoint index\" or start working.";
    connection = null!;

    if (!File.Exists(_context.Paths.DbPath))
    {
      return false;
    }

    connection = WaypointDb.Open(_context.Paths.DbPath);
    return true;
  }

  private long? ResolveTaskId(SqliteConnection connection, long? taskIdArg, string branch)
  {
    if (taskIdArg.HasValue)
    {
      return TaskExists(connection, taskIdArg.Value) ? taskIdArg : null;
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM task
      WHERE repo_root = $repo_root
        AND (branch = $branch OR (branch IS NULL AND $branch IS NULL))
        AND status = 'active'
      ORDER BY created_at DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$repo_root", _context.RepoRoot);
    command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
    var result = command.ExecuteScalar();
    return result is long taskId ? taskId : null;
  }

  private static bool TaskExists(SqliteConnection connection, long taskId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM task WHERE id = $id);";
    command.Parameters.AddWithValue("$id", taskId);
    return command.ExecuteScalar() is long found && found == 1;
  }

  private static string NoActiveTaskMessage(string branch) =>
    $"No active Waypoint task on {branch}. Start by sending a prompt in this repo.";

  private static string? ResolveSessionId(SqliteConnection connection, long taskId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM session
      WHERE task_id = $task_id AND ended_at IS NULL
      ORDER BY started_at DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var openSession = command.ExecuteScalar() as string;
    if (!string.IsNullOrWhiteSpace(openSession))
    {
      return openSession;
    }

    command.CommandText = """
      SELECT id
      FROM session
      WHERE task_id = $task_id
      ORDER BY started_at DESC
      LIMIT 1;
      """;
    var recentSession = command.ExecuteScalar() as string;
    return string.IsNullOrWhiteSpace(recentSession) ? null : recentSession;
  }

  private static void MarkBriefInjectedViaMcp(SqliteConnection connection, string sessionId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      UPDATE session
      SET brief_injected_via = 'mcp'
      WHERE id = $id
        AND (brief_injected_via IS NULL OR brief_injected_via = '');
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.ExecuteNonQuery();
  }

  private static long? FindLatestClaimId(SqliteConnection connection, long taskId, string sessionId, string kind, string text)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM claim
      WHERE task_id = $task_id
        AND session_id = $session_id
        AND kind = $kind
        AND text = $text
      ORDER BY id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$session_id", sessionId);
    command.Parameters.AddWithValue("$kind", kind);
    command.Parameters.AddWithValue("$text", text);
    var result = command.ExecuteScalar();
    return result is long id ? id : null;
  }

  private static string ShortMessage(string message)
  {
    if (string.IsNullOrWhiteSpace(message))
    {
      return "Unknown error.";
    }

    var clean = message.Replace('\n', ' ').Replace('\r', ' ').Trim();
    return clean.Length <= 140 ? clean : clean[..140];
  }

  private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, int>> BuildSessionOrdinalsByTask(
    SqliteConnection connection,
    IEnumerable<long> taskIds)
  {
    var result = new Dictionary<long, IReadOnlyDictionary<string, int>>();
    foreach (var taskId in taskIds.Distinct())
    {
      result[taskId] = BuildSessionOrdinals(connection, taskId);
    }

    return result;
  }

  private static IReadOnlyDictionary<string, int> BuildSessionOrdinals(SqliteConnection connection, long taskId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM session
      WHERE task_id = $task_id
      ORDER BY started_at ASC, id ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();

    var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
    var ordinal = 1;
    while (reader.Read())
    {
      ordinals[reader.GetString(0)] = ordinal;
      ordinal++;
    }

    return ordinals;
  }
}
