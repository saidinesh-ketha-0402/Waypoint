using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Brief;

public sealed class BriefRenderer
{
  private const int TokenBudget = 1500;

  private readonly SqliteConnection _connection;
  private readonly ITokenCounter _tokenCounter;
  private readonly Ranker _ranker;
  private readonly TimeProvider _timeProvider;

  public BriefRenderer(
    SqliteConnection connection,
    ITokenCounter? tokenCounter = null,
    Ranker? ranker = null,
    TimeProvider? timeProvider = null)
  {
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    _timeProvider = timeProvider ?? TimeProvider.System;
    _tokenCounter = tokenCounter ?? new CharsDiv4TokenCounter();
    _ranker = ranker ?? new Ranker(_timeProvider);
  }

  public string? RenderForActiveTask(string repoRoot, string? branch)
  {
    var taskId = FindActiveTaskId(repoRoot, branch);
    return taskId.HasValue ? RenderForTask(taskId.Value) : null;
  }

  public string? RenderForTask(long taskId)
  {
    var task = LoadTask(taskId);
    if (task is null)
    {
      return null;
    }

    var sessionOrdinals = LoadSessionOrdinals(taskId);
    var previousSessionTouchedNodes = LoadPreviousSessionTouchedNodeIds(taskId, sessionOrdinals);
    var rankedClaims = _ranker.RankClaims(LoadCurrentClaims(taskId), previousSessionTouchedNodes);
    var doneItems = LoadDoneItems(taskId, sessionOrdinals);
    var hotFiles = LoadHotFiles(taskId);
    var statusLine = BuildStatusLine(taskId);
    var latestSession = sessionOrdinals.Values.DefaultIfEmpty(1).Max();
    var lastActiveHours = Math.Max(0, (int)Math.Floor((_timeProvider.GetUtcNow().UtcDateTime - ParseIso(task.Value.UpdatedAtUtcIso)).TotalHours));

    var sections = new[]
    {
      BuildGoalStatusSection(task.Value.GoalText, statusLine),
      BuildDoneSection(doneItems),
      BuildClaimSection("**Decisions & constraints still in force.**", rankedClaims, sessionOrdinals, ["decision", "constraint"], 300, numbered: false),
      BuildClaimSection("**Tried and failed — do not retry.**", rankedClaims, sessionOrdinals, ["attempt_failed"], 250, numbered: false),
      BuildClaimSection("**Open items / next steps (ordered).**", rankedClaims, sessionOrdinals, ["open_item"], 250, numbered: true),
      BuildHotFilesSection(hotFiles),
      BuildFixedLineSection("Drill down: waypoint.why(<symbol>), waypoint.impact(<symbol>), waypoint.what_changed(), waypoint.open_items()")
    };

    return RenderWithBudget(
      $"## Waypoint resume brief — task: {task.Value.Title}   (session {latestSession} of this task · last active {lastActiveHours}h ago · branch {task.Value.Branch})",
      sections);
  }

  private long? FindActiveTaskId(string repoRoot, string? branch)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM task
      WHERE repo_root = $repo_root
        AND (branch = $branch OR (branch IS NULL AND $branch IS NULL))
        AND status = 'active'
      ORDER BY created_at DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$repo_root", repoRoot);
    command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
    var result = command.ExecuteScalar();
    return result is long taskId ? taskId : null;
  }

  private TaskRow? LoadTask(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT title, goal_text, branch, updated_at
      FROM task
      WHERE id = $id
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$id", taskId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    var title = reader.GetString(0);
    var goalText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
    var branch = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
    var updatedAtIso = reader.IsDBNull(3) ? ToUtcIso(_timeProvider.GetUtcNow()) : reader.GetString(3);
    return new TaskRow(taskId, title, goalText, branch, updatedAtIso);
  }

  private Dictionary<string, int> LoadSessionOrdinals(long taskId)
  {
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT s.id
      FROM session s
      WHERE s.task_id = $task_id
        AND EXISTS (SELECT 1 FROM episode e WHERE e.session_id = s.id)
      ORDER BY s.started_at ASC, s.id ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);

    using var reader = command.ExecuteReader();
    var ordinal = 1;
    while (reader.Read())
    {
      result[reader.GetString(0)] = ordinal;
      ordinal++;
    }

    return result;
  }

  private ISet<long> LoadPreviousSessionTouchedNodeIds(long taskId, IReadOnlyDictionary<string, int> sessionOrdinals)
  {
    var latest = sessionOrdinals.Values.DefaultIfEmpty(1).Max();
    if (latest <= 1)
    {
      return new HashSet<long>();
    }

    string? previousSessionId = null;
    foreach (var pair in sessionOrdinals)
    {
      if (pair.Value == latest - 1)
      {
        previousSessionId = pair.Key;
        break;
      }
    }

    if (previousSessionId is null)
    {
      return new HashSet<long>();
    }

    var nodes = new HashSet<long>();
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT et.node_id
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      WHERE s.task_id = $task_id
        AND s.id = $session_id;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$session_id", previousSessionId);
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      nodes.Add(reader.GetInt64(0));
    }

    return nodes;
  }

  private IReadOnlyList<ClaimRecord> LoadCurrentClaims(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, text, confidence, source, about_node_id, created_at, session_id
      FROM claim
      WHERE task_id = $task_id
        AND status = 'current'
      ORDER BY id DESC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var claims = new List<ClaimRecord>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      claims.Add(new ClaimRecord(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDouble(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7)));
    }

    return claims;
  }

  private IReadOnlyList<DoneItem> LoadDoneItems(long taskId, IReadOnlyDictionary<string, int> sessionOrdinals)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT n.id, n.name, n.qualified_name, COALESCE(n.project, ''), COUNT(*) AS edit_count
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
        AND et.how = 'edited'
      GROUP BY n.id, n.name, n.qualified_name, n.project
      ORDER BY edit_count DESC, n.id DESC
      LIMIT 16;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var items = new List<DoneItem>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      var nodeId = reader.GetInt64(0);
      var display = reader.GetString(1);
      var qualifiedName = reader.GetString(2);
      var project = reader.GetString(3);
      var editCount = reader.GetInt32(4);
      var decision = LoadBestDecisionForNode(taskId, nodeId, sessionOrdinals);
      items.Add(new DoneItem(display, qualifiedName, project, editCount, decision.Text, decision.SessionTag));
    }

    return items;
  }

  private (string Text, string SessionTag) LoadBestDecisionForNode(long taskId, long nodeId, IReadOnlyDictionary<string, int> sessionOrdinals)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT text, session_id, confidence
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'decision'
        AND status = 'current'
        AND about_node_id = $node_id
      ORDER BY confidence DESC, id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$node_id", nodeId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return ("no explicit decision captured yet", string.Empty);
    }

    var text = reader.GetString(0);
    var sessionId = reader.IsDBNull(1) ? null : reader.GetString(1);
    return (text, SessionTag(sessionId, sessionOrdinals));
  }

  private IReadOnlyList<HotFile> LoadHotFiles(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT n.name, COUNT(*) AS touch_count
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
        AND n.kind = 'file'
      GROUP BY n.name
      ORDER BY touch_count DESC, n.name ASC
      LIMIT 8;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var files = new List<HotFile>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      files.Add(new HotFile(reader.GetString(0), reader.GetInt32(1)));
    }

    return files;
  }

  private string BuildStatusLine(long taskId)
  {
    var editedFileCount = ScalarCount("""
      SELECT COUNT(DISTINCT n.id)
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
        AND et.how = 'edited'
        AND n.kind = 'file';
      """, taskId);

    var editedProjectCount = ScalarCount("""
      SELECT COUNT(DISTINCT n.project)
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
        AND et.how = 'edited'
        AND n.project IS NOT NULL
        AND n.project <> '';
      """, taskId);

    var shellStatus = ReadLatestShellStatus(taskId);
    var migrationCount = ScalarCount("""
      SELECT COUNT(*)
      FROM node n
      WHERE n.kind = 'migration'
        AND n.qualified_name LIKE 'mig:pending:%';
      """, taskId, appliesTaskFilter: false);
    var sawEfUpdate = DidSeeEfDatabaseUpdate(taskId);

    var build = shellStatus.BuildStatus ?? "unknown";
    var testText = shellStatus.TestsStatus ?? "unknown";
    var migrationText = migrationCount == 0
      ? "0 added"
      : sawEfUpdate
        ? $"{migrationCount} added, applied"
        : $"{migrationCount} added, not applied";
    return $"in progress · {editedFileCount} files in {editedProjectCount} projects changed · build: {build} · tests: {testText} · migrations: {migrationText}";
  }

  private ShellStatus ReadLatestShellStatus(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT e.payload_json
      FROM episode e
      INNER JOIN session s ON s.id = e.session_id
      WHERE s.task_id = $task_id
        AND e.kind = 'shell'
      ORDER BY e.id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var payload = command.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(payload))
    {
      return new ShellStatus(null, null);
    }

    try
    {
      using var doc = JsonDocument.Parse(payload);
      if (!doc.RootElement.TryGetProperty("shell", out var shell) || shell.ValueKind != JsonValueKind.Object)
      {
        return new ShellStatus(null, null);
      }

      var build = TryReadString(shell, "build");
      var buildStatus = string.IsNullOrWhiteSpace(build)
        ? null
        : string.Equals(build, "ok", StringComparison.OrdinalIgnoreCase) ? "OK" : "FAILED";

      string? testsStatus = null;
      if (shell.TryGetProperty("tests", out var testsObj) && testsObj.ValueKind == JsonValueKind.Object)
      {
        var passed = TryReadInt(testsObj, "passed");
        var failed = TryReadInt(testsObj, "failed");
        var skipped = TryReadInt(testsObj, "skipped");
        var total = passed + failed + skipped;
        var state = failed == 0 ? "PASS" : "FAIL";
        var commandText = TryReadString(doc.RootElement, "command");
        testsStatus = $"{passed}/{total} {state}";
        if (!string.IsNullOrWhiteSpace(commandText))
        {
          testsStatus += $" (`{TrimSentence(commandText, 60)}`)";
        }
      }

      return new ShellStatus(buildStatus, testsStatus);
    }
    catch (JsonException)
    {
      return new ShellStatus(null, null);
    }
  }

  private bool DidSeeEfDatabaseUpdate(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT payload_json
      FROM episode e
      INNER JOIN session s ON s.id = e.session_id
      WHERE s.task_id = $task_id
        AND e.kind = 'shell'
      ORDER BY e.id DESC
      LIMIT 20;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      if (reader.IsDBNull(0))
      {
        continue;
      }

      var payload = reader.GetString(0);
      if (payload.Contains("\"ef_database_update\":true", StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private int ScalarCount(string sql, long taskId, bool appliesTaskFilter = true)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = sql;
    if (appliesTaskFilter)
    {
      command.Parameters.AddWithValue("$task_id", taskId);
    }

    var result = command.ExecuteScalar();
    return result is long count ? (int)count : 0;
  }

  private static Section BuildGoalStatusSection(string goalText, string statusLine) =>
    new("Goal & status", 250, [$"**Goal.** {TrimWords(goalText, 60)}", $"**Status.** {statusLine}"]);

  private Section BuildDoneSection(IReadOnlyList<DoneItem> doneItems)
  {
    var lines = new List<string> { "**Done so far (what & why).**" };
    if (doneItems.Count == 0)
    {
      lines.Add("- none");
      return new Section("Done so far", 300, lines);
    }

    foreach (var item in doneItems)
    {
      var symbol = item.Project.Length == 0 ? item.DisplayName : $"{item.Project}/{item.DisplayName}";
      var reason = TrimSentence(item.DecisionText, 110);
      var sessionSuffix = item.DecisionSessionTag.Length == 0 ? string.Empty : $" {item.DecisionSessionTag}";
      lines.Add($"- `{symbol}` — edited {item.EditCount}x — because {reason}{sessionSuffix}");
    }

    return new Section("Done so far", 300, lines);
  }

  private Section BuildClaimSection(
    string heading,
    IReadOnlyList<RankedClaim> rankedClaims,
    IReadOnlyDictionary<string, int> sessionOrdinals,
    IReadOnlyList<string> kinds,
    int softCap,
    bool numbered)
  {
    var lines = new List<string> { heading };
    var filtered = rankedClaims
      .Where(claim => kinds.Contains(claim.Claim.Kind, StringComparer.Ordinal))
      .Take(40)
      .ToArray();

    if (filtered.Length == 0)
    {
      lines.Add("- none");
      return new Section(heading, softCap, lines);
    }

    var index = 1;
    foreach (var ranked in filtered)
    {
      var tag = SessionTag(ranked.Claim.SessionId, sessionOrdinals);
      var prefix = numbered ? $"{index}. " : "- ";
      lines.Add($"{prefix}{TrimSentence(ranked.Claim.Text, 180)}{(tag.Length == 0 ? string.Empty : $"  {tag}")}");
      index++;
    }

    return new Section(heading, softCap, lines);
  }

  private Section BuildHotFilesSection(IReadOnlyList<HotFile> hotFiles)
  {
    var lines = new List<string>();
    if (hotFiles.Count == 0)
    {
      lines.Add("**Hot files.** none");
      return new Section("Hot files", 100, lines);
    }

    var joined = string.Join(" · ", hotFiles.Select(file => $"`{file.FilePath}` (edited {file.TouchCount}x)"));
    lines.Add($"**Hot files.** {joined}");

    return new Section("Hot files", 100, lines);
  }

  private static Section BuildFixedLineSection(string line) =>
    new("Drill down", 50, [line]);

  private string RenderWithBudget(string title, IReadOnlyList<Section> sections)
  {
    var builder = new StringBuilder();
    var remaining = TokenBudget;

    builder.AppendLine(title);
    builder.AppendLine();
    remaining -= _tokenCounter.CountTokens(title + Environment.NewLine + Environment.NewLine);

    var carryForward = 0;
    foreach (var section in sections)
    {
      if (remaining <= 0)
      {
        break;
      }

      var allowed = Math.Min(remaining, section.SoftCap + carryForward);
      var rendered = RenderSection(section, allowed, out var usedTokens);
      if (rendered.Length == 0)
      {
        continue;
      }

      builder.Append(rendered);
      remaining -= usedTokens;
      carryForward = Math.Max(0, (section.SoftCap + carryForward) - usedTokens);
    }

    return builder.ToString().TrimEnd();
  }

  private string RenderSection(Section section, int tokenLimit, out int usedTokens)
  {
    usedTokens = 0;
    if (tokenLimit <= 0 || section.Lines.Count == 0)
    {
      return string.Empty;
    }

    var builder = new StringBuilder();
    var visibleLines = 0;
    for (var i = 0; i < section.Lines.Count; i++)
    {
      var line = section.Lines[i];
      var candidate = line + Environment.NewLine;
      var candidateTokens = _tokenCounter.CountTokens(candidate);
      if (i > 0 && usedTokens + candidateTokens > tokenLimit)
      {
        break;
      }

      if (i == 0 && usedTokens + candidateTokens > tokenLimit)
      {
        return string.Empty;
      }

      builder.Append(candidate);
      usedTokens += candidateTokens;
      visibleLines++;
    }

    if (visibleLines == 0)
    {
      return string.Empty;
    }

    builder.AppendLine();
    usedTokens += _tokenCounter.CountTokens(Environment.NewLine);
    return builder.ToString();
  }

  private static string TrimWords(string text, int maxWords)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return "n/a";
    }

    var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length <= maxWords)
    {
      return text.Trim();
    }

    return string.Join(' ', words.Take(maxWords)) + " …";
  }

  private static string TrimSentence(string text, int maxChars)
  {
    var trimmed = text.Trim();
    if (trimmed.Length <= maxChars)
    {
      return trimmed;
    }

    return trimmed[..maxChars] + " …";
  }

  private static string SessionTag(string? sessionId, IReadOnlyDictionary<string, int> ordinals)
  {
    if (sessionId is null || !ordinals.TryGetValue(sessionId, out var ordinal))
    {
      return string.Empty;
    }

    return $"[s{ordinal}]";
  }

  private static string? TryReadString(JsonElement obj, string name)
  {
    if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
    {
      return value.GetString();
    }

    return null;
  }

  private static int TryReadInt(JsonElement obj, string name)
  {
    if (!obj.TryGetProperty(name, out var value))
    {
      return 0;
    }

    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
    {
      return numericValue;
    }

    if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedValue))
    {
      return parsedValue;
    }

    return 0;
  }

  private static DateTime ParseIso(string value) =>
    DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

  private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

  private readonly record struct TaskRow(long Id, string Title, string GoalText, string Branch, string UpdatedAtUtcIso);
  private readonly record struct DoneItem(string DisplayName, string QualifiedName, string Project, int EditCount, string DecisionText, string DecisionSessionTag);
  private readonly record struct HotFile(string FilePath, int TouchCount);
  private readonly record struct Section(string Name, int SoftCap, IReadOnlyList<string> Lines);
  private readonly record struct ShellStatus(string? BuildStatus, string? TestsStatus);
}
