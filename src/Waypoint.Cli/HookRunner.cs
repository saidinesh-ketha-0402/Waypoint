using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Waypoint.Core.Extraction;
using Waypoint.Core.Indexing;
using Waypoint.Core.Logging;
using Waypoint.Core.Parsing;
using Waypoint.Core.Paths;
using Waypoint.Core.Queries;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Syntax;

namespace Waypoint.Cli;

public interface IBackgroundProcessLauncher
{
  void LaunchIndex(string repoRoot);
}

public sealed record HookRunnerOptions(
  bool InjectBriefOnce = false,
  IBackgroundProcessLauncher? BackgroundProcessLauncher = null);

public static class HookRunner
{
  private const string SessionStartEvent = "sessionStart";
  private const string BeforeSubmitPromptEvent = "beforeSubmitPrompt";
  private const string PostToolUseEvent = "postToolUse";
  private const string AfterFileEditEvent = "afterFileEdit";
  private const string AfterAgentResponseEvent = "afterAgentResponse";
  private const string AfterAgentThoughtEvent = "afterAgentThought";
  private const string PreCompactEvent = "preCompact";
  private const string StopEvent = "stop";
  private const string SessionEndEvent = "sessionEnd";
  private const int CheckpointTailChars = 200 * 1024;
  private const string TranscriptOffsetMetaPrefix = "transcript_offset:";

  public static async Task<int> RunAsync(
    string eventName,
    Stream stdin,
    Stream stdout,
    HookRunnerOptions options,
    CancellationToken cancellationToken = default)
  {
    var normalizedEventName = NormalizeEventName(eventName);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var defaultResponse = IsBeforeSubmitPrompt(normalizedEventName) ? """{"continue":true}""" : "{}";

    HookPayloadEnvelope? payload = null;
    WaypointPaths? paths = null;

    try
    {
      payload = await ReadPayloadAsync(stdin, cancellationToken);
      var repoRoot = ResolveRepoRoot(payload);
      paths = WaypointPaths.ForRepo(repoRoot);
      var useExplicitTransaction = !string.Equals(normalizedEventName, AfterFileEditEvent, StringComparison.Ordinal);

      using var connection = WaypointDb.Open(paths.DbPath);
      using var transaction = useExplicitTransaction ? connection.BeginTransaction() : null;
      var traceStore = new TraceStore(connection);
      var graphStore = new GraphStore(connection);

      var sessionId = ResolveSessionId(payload);
      traceStore.EnsureSession(sessionId, model: payload.Model);

      var response = HandleEvent(normalizedEventName, payload, traceStore, graphStore, connection, repoRoot, sessionId, options);

      transaction?.Commit();
      await WriteResponseAsync(stdout, response, cancellationToken);

      HookLog.Append(paths.LogPath, normalizedEventName, new
      {
        conv = payload.ConversationId,
        session = sessionId,
        ms = stopwatch.ElapsedMilliseconds,
        ok = true
      });
    }
    catch (Exception exception)
    {
      if (paths is null)
      {
        var fallbackRepo = FindGitRootFromCwd() ?? Directory.GetCurrentDirectory();
        paths = WaypointPaths.ForRepo(fallbackRepo);
      }

      HookLog.Append(paths.LogPath, normalizedEventName, new
      {
        conv = payload?.ConversationId,
        session = payload?.SessionId,
        ms = stopwatch.ElapsedMilliseconds,
        ok = false,
        error = exception.GetType().Name,
        message = exception.Message
      });

      await WriteResponseAsync(stdout, defaultResponse, cancellationToken);
    }

    return 0;
  }

  private static string HandleEvent(
    string eventName,
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    HookRunnerOptions options)
  {
    if (string.Equals(eventName, SessionStartEvent, StringComparison.Ordinal))
    {
      return HandleSessionStart(payload, traceStore, connection, repoRoot, sessionId);
    }

    if (string.Equals(eventName, BeforeSubmitPromptEvent, StringComparison.Ordinal))
    {
      return HandleBeforeSubmitPrompt(payload, traceStore, repoRoot, sessionId);
    }

    if (string.Equals(eventName, AfterFileEditEvent, StringComparison.Ordinal))
    {
      return HandleAfterFileEdit(payload, traceStore, graphStore, connection, repoRoot, sessionId);
    }

    if (string.Equals(eventName, PostToolUseEvent, StringComparison.Ordinal))
    {
      return HandlePostToolUse(payload, traceStore, graphStore, connection, repoRoot, sessionId, options);
    }

    if (string.Equals(eventName, SessionEndEvent, StringComparison.Ordinal))
    {
      return HandleSessionEnd(payload, traceStore, sessionId);
    }

    if (string.Equals(eventName, AfterAgentResponseEvent, StringComparison.Ordinal))
    {
      return HandleAfterAgentResponse(payload, traceStore, connection, repoRoot, sessionId);
    }

    if (string.Equals(eventName, AfterAgentThoughtEvent, StringComparison.Ordinal))
    {
      return HandleAfterAgentThought(payload, traceStore, connection, repoRoot, sessionId);
    }

    if (string.Equals(eventName, PreCompactEvent, StringComparison.Ordinal))
    {
      return HandlePreCompact(payload, traceStore, connection, repoRoot, sessionId);
    }

    if (string.Equals(eventName, StopEvent, StringComparison.Ordinal))
    {
      return HandleStop(payload, traceStore, graphStore, connection, repoRoot, sessionId, options);
    }

    _ = options;
    return "{}";
  }

  private static string HandleSessionStart(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var branch = GitInfo.CurrentBranch(repoRoot);
    var activeTaskId = traceStore.FindActiveTaskId(repoRoot, branch);
    if (!activeTaskId.HasValue)
    {
      return "{}";
    }

    traceStore.SetSessionTask(sessionId, activeTaskId.Value, payload.Model);
    var brief = BriefBuilder.RenderForTask(connection, activeTaskId.Value);
    if (string.IsNullOrWhiteSpace(brief))
    {
      return "{}";
    }

    traceStore.MarkBriefInjected(sessionId, SessionStartEvent);

    var result = new HookAdditionalContextResponse(
      brief,
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["WAYPOINT_TASK_ID"] = activeTaskId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
      });
    return JsonSerializer.Serialize(result, HookRunnerJsonContext.Default.HookAdditionalContextResponse);
  }

  private static string HandleBeforeSubmitPrompt(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    string repoRoot,
    string sessionId)
  {
    var prompt = payload.GetFirstString("prompt", "text", "user_message") ?? string.Empty;
    var branch = GitInfo.CurrentBranch(repoRoot);
    var taskId = traceStore.EnsureTaskForPrompt(repoRoot, branch, prompt);
    traceStore.SetSessionTask(sessionId, taskId, payload.Model);

    var summary = prompt.Length <= 120 ? prompt : prompt[..120];
    var payloadJson = payload.GetRawPayloadJson();
    traceStore.AddEpisode(sessionId, "prompt", payloadJson, summary);

    return JsonSerializer.Serialize(new HookContinueResponse(true), HookRunnerJsonContext.Default.HookContinueResponse);
  }

  private static string HandleSessionEnd(HookPayloadEnvelope payload, TraceStore traceStore, string sessionId)
  {
    var reason = payload.GetFirstString("reason", "final_status", "status");
    traceStore.EndSession(sessionId, reason);
    return "{}";
  }

  private static string HandleAfterAgentResponse(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var responseText = payload.GetFirstString(
      "response_text",
      "response",
      "assistant_response",
      "assistant_text",
      "text") ?? string.Empty;
    var payloadPreview = JsonSerializer.Serialize(new
    {
      response_text = Truncate(responseText, 4096)
    });
    var summary = BuildResponseSummary(responseText);
    var episodeId = traceStore.AddEpisode(sessionId, "response", payloadPreview, summary, responseText);
    var taskId = EnsureSessionTask(traceStore, connection, repoRoot, sessionId);
    if (!taskId.HasValue || string.IsNullOrWhiteSpace(responseText))
    {
      return "{}";
    }

    var resolver = new SymbolResolver(connection);
    var classifier = new ClaimClassifier(new SymbolLinker(resolver), new SentenceSplitter());
    var claims = classifier.Classify(responseText, source: "regex");
    var writer = new ClaimWriter(connection);
    writer.WriteClaims(taskId.Value, sessionId, episodeId, claims);
    return "{}";
  }

  private static string HandleAfterAgentThought(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var thoughtText = payload.GetFirstString(
      "thought_text",
      "thought",
      "assistant_thought",
      "response_text",
      "response",
      "text") ?? string.Empty;
    var payloadPreview = JsonSerializer.Serialize(new
    {
      thought_text = Truncate(thoughtText, 4096)
    });
    var summary = BuildResponseSummary(thoughtText);
    var episodeId = traceStore.AddEpisode(sessionId, "thought", payloadPreview, summary, thoughtText);
    var taskId = EnsureSessionTask(traceStore, connection, repoRoot, sessionId);
    if (!taskId.HasValue || string.IsNullOrWhiteSpace(thoughtText))
    {
      return "{}";
    }

    var resolver = new SymbolResolver(connection);
    var classifier = new ClaimClassifier(new SymbolLinker(resolver), new SentenceSplitter());
    var claims = classifier.Classify(thoughtText, source: "regex", fromThought: true);
    var writer = new ClaimWriter(connection);
    writer.WriteClaims(taskId.Value, sessionId, episodeId, claims);
    return "{}";
  }

  private static string HandlePreCompact(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var contextUsagePercent = TryGetInt32(payload.Extra, "context_usage_percent", "contextUsagePercent");
    var contextTokens = TryGetInt32(payload.Extra, "context_tokens", "contextTokens");
    var trigger = payload.GetFirstString("trigger", "reason", "compact_reason", "compactReason");
    var compactSummary = contextUsagePercent.HasValue
      ? $"compact {contextUsagePercent.Value}%"
      : "compact";
    var compactEpisodeId = traceStore.AddEpisode(
      sessionId,
      "compact",
      JsonSerializer.Serialize(new
      {
        context_usage_percent = contextUsagePercent,
        context_tokens = contextTokens,
        trigger
      }),
      compactSummary);

    _ = RunCheckpoint(payload, traceStore, connection, repoRoot, sessionId, compactEpisodeId);
    var taskId = EnsureSessionTask(traceStore, connection, repoRoot, sessionId);
    var counts = CountCheckpointClaims(connection, taskId);
    var message = $"Waypoint checkpoint: {counts.DecisionAndConstraintCount} decisions, {counts.OpenItemCount} open items saved";
    return JsonSerializer.Serialize(new HookUserMessageResponse(message), HookRunnerJsonContext.Default.HookUserMessageResponse);
  }

  private static string HandleStop(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    HookRunnerOptions options)
  {
    _ = graphStore;
    _ = RunCheckpoint(payload, traceStore, connection, repoRoot, sessionId, null);

    var hadCsEditSincePreviousStop = HasCsEditSincePreviousStop(connection, sessionId);
    var status = payload.GetFirstString("status", "reason", "final_status", "finalStatus");
    var loopCount = TryGetInt32(payload.Extra, "loop_count", "loopCount");
    traceStore.AddEpisode(
      sessionId,
      "stop",
      JsonSerializer.Serialize(new
      {
        status,
        loop_count = loopCount
      }),
      "stop");

    UpdateSessionTokenCounters(connection, sessionId, payload);
    WriteResumeFile(traceStore, connection, repoRoot, sessionId);

    if (hadCsEditSincePreviousStop)
    {
      var launcher = options.BackgroundProcessLauncher ?? DefaultBackgroundProcessLauncher.Instance;
      launcher.LaunchIndex(repoRoot);
    }

    return "{}";
  }

  private static string HandleAfterFileEdit(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var absoluteFilePath = payload.GetFirstString("file_path", "path", "target_file", "relative_path");
    if (string.IsNullOrWhiteSpace(absoluteFilePath))
    {
      traceStore.AddEpisode(sessionId, "edit", summary: "edit unknown file");
      return "{}";
    }

    var normalizedAbsolute = Path.GetFullPath(Path.IsPathRooted(absoluteFilePath)
      ? absoluteFilePath
      : Path.Combine(repoRoot, absoluteFilePath));
    var relativePath = ToRepoRelativePath(repoRoot, normalizedAbsolute);
    var edits = ReadEdits(payload).ToArray();
    var episodePayload = new
    {
      file = relativePath,
      edit_count = edits.Length,
      edits = edits.Select(edit => new
      {
        old_string = Truncate(edit.OldString, 200),
        new_string = Truncate(edit.NewString, 200)
      })
    };
    var episodeId = traceStore.AddEpisode(
      sessionId,
      "edit",
      JsonSerializer.Serialize(episodePayload),
      $"edit {Path.GetFileName(relativePath)}");

    if (!File.Exists(normalizedAbsolute))
    {
      TouchFileNode(graphStore, connection, relativePath, episodeId);
      return "{}";
    }

    if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
    {
      TouchFileNode(graphStore, connection, relativePath, episodeId);
      return "{}";
    }

    var fileText = File.ReadAllText(normalizedAbsolute);
    var refresher = new SyntaxRefresher(connection);
    var refreshResult = refresher.Refresh(new FileRefreshRequest(repoRoot, relativePath, fileText));
    var touchedNodeIds = new HashSet<long>();
    var searchStart = 0;
    foreach (var edit in edits)
    {
      if (string.IsNullOrWhiteSpace(edit.NewString))
      {
        continue;
      }

      var index = fileText.IndexOf(edit.NewString, searchStart, StringComparison.Ordinal);
      if (index < 0)
      {
        continue;
      }

      var startLine = GetLineNumberAtOffset(fileText, index);
      var endLine = GetLineNumberAtOffset(fileText, index + edit.NewString.Length);
      foreach (var node in refreshResult.NodesInRange(startLine, endLine))
      {
        touchedNodeIds.Add(node.NodeId);
      }

      searchStart = index + edit.NewString.Length;
    }

    if (touchedNodeIds.Count == 0)
    {
      TouchFileNode(graphStore, connection, relativePath, episodeId);
      return "{}";
    }

    foreach (var nodeId in touchedNodeIds)
    {
      InsertTouch(connection, episodeId, nodeId, "edited");
    }

    return "{}";
  }

  private static string HandlePostToolUse(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    HookRunnerOptions options)
  {
    var toolName = payload.GetFirstString("tool_name", "toolName") ?? "unknown";
    var normalizedTool = toolName.Trim();
    if (normalizedTool.Length == 0)
    {
      normalizedTool = "unknown";
    }

    var spanIndex = new SpanIndex(connection);
    if (string.Equals(normalizedTool, "Read", StringComparison.OrdinalIgnoreCase))
    {
      HandlePostToolUseRead(payload, traceStore, graphStore, connection, repoRoot, sessionId, spanIndex);
    }
    else if (string.Equals(normalizedTool, "Grep", StringComparison.OrdinalIgnoreCase))
    {
      HandlePostToolUseGrep(payload, traceStore, connection, repoRoot, sessionId, spanIndex);
    }
    else if (string.Equals(normalizedTool, "Glob", StringComparison.OrdinalIgnoreCase))
    {
      HandlePostToolUseGlob(payload, traceStore, sessionId);
    }
    else if (string.Equals(normalizedTool, "Shell", StringComparison.OrdinalIgnoreCase))
    {
      HandlePostToolUseShell(payload, traceStore, graphStore, connection, repoRoot, sessionId);
    }
    else
    {
      traceStore.AddEpisode(sessionId, "tool", payload.GetRawPayloadJson(), $"tool {normalizedTool}");
    }

    if (!options.InjectBriefOnce)
    {
      return "{}";
    }

    return TryBuildPostToolUseFallbackBrief(traceStore, connection, repoRoot, sessionId) ?? "{}";
  }

  private static void HandlePostToolUseRead(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    SpanIndex spanIndex)
  {
    var input = ReadToolInput(payload);
    var readPath = TryGetString(input, "path", "file_path", "filePath");
    var relativePath = ToRepoRelativePathSafe(repoRoot, readPath);
    var offset = TryGetInt32(input, "offset", "line", "start_line", "startLine");
    var limit = TryGetInt32(input, "limit", "length", "line_count", "lineCount");
    var summary = $"read {Path.GetFileName(relativePath ?? "unknown")}";
    var episodeId = traceStore.AddEpisode(
      sessionId,
      "read",
      JsonSerializer.Serialize(new
      {
        path = relativePath,
        offset,
        limit
      }),
      summary);

    if (string.IsNullOrWhiteSpace(relativePath))
    {
      return;
    }

    if (!offset.HasValue || !limit.HasValue)
    {
      TouchFileNodeWithHow(graphStore, connection, relativePath, episodeId, "read");
      return;
    }

    var endLine = Math.Max(offset.Value, offset.Value + Math.Max(limit.Value, 1) - 1);
    var nodes = spanIndex.NodesInRange(relativePath, Math.Max(offset.Value, 1), endLine);
    if (nodes.Count == 0)
    {
      TouchFileNodeWithHow(graphStore, connection, relativePath, episodeId, "read");
      return;
    }

    foreach (var node in nodes)
    {
      InsertTouch(connection, episodeId, node.NodeId, "read");
    }
  }

  private static void HandlePostToolUseGrep(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    SpanIndex spanIndex)
  {
    var input = ReadToolInput(payload);
    var pattern = TryGetString(input, "pattern", "query") ?? string.Empty;
    var output = payload.GetFirstString("tool_output", "toolOutput", "output") ?? string.Empty;
    var matches = ParseGrepMatches(output, repoRoot).Take(50).ToArray();
    var episodeId = traceStore.AddEpisode(
      sessionId,
      "grep",
      JsonSerializer.Serialize(new { pattern, match_count = matches.Length }),
      $"grep {pattern} ({matches.Length})");

    foreach (var match in matches)
    {
      var node = spanIndex.FindEnclosing(match.Path, match.Line);
      if (node is not null)
      {
        InsertTouch(connection, episodeId, node.NodeId, "matched");
      }
    }
  }

  private static void HandlePostToolUseGlob(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    string sessionId)
  {
    var input = ReadToolInput(payload);
    var pattern = TryGetString(input, "glob_pattern", "globPattern", "pattern") ?? string.Empty;
    var output = payload.GetFirstString("tool_output", "toolOutput", "output") ?? string.Empty;
    var resultCount = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    traceStore.AddEpisode(
      sessionId,
      "glob",
      JsonSerializer.Serialize(new { pattern, result_count = resultCount }),
      $"glob {pattern} ({resultCount})");
  }

  private static void HandlePostToolUseShell(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var input = ReadToolInput(payload);
    var command = TryGetString(input, "command", "cmd") ?? string.Empty;
    var output = payload.GetFirstString("tool_output", "toolOutput", "output") ?? string.Empty;
    var exitCode = TryGetInt32(payload.Extra, "exit_code", "exitCode", "tool_exit_code", "toolExitCode")
      ?? TryGetInt32(input, "exit_code", "exitCode");

    var parser = new DotnetOutputParser(repoRoot);
    var shellResult = parser.Parse(command, output, exitCode);
    var outputPreview = Truncate(output, 500);
    var parsed = shellResult is null
      ? null
      : new
      {
        dotnet_command = shellResult.DotnetCommand,
        build = shellResult.Build,
        failed = shellResult.Failed,
        errors = shellResult.Errors.Select(error => new { error.File, error.Line, error.Code, error.Message }).ToArray(),
        tests = shellResult.Tests is null
          ? null
          : new
          {
            passed = shellResult.Tests.Passed,
            failed = shellResult.Tests.Failed,
            skipped = shellResult.Tests.Skipped,
            failed_names = shellResult.Tests.FailedNames
          },
        exception = shellResult.Exception,
        di_error = shellResult.DiError,
        ef = shellResult.Ef,
        ef_database_update = command.Contains("dotnet ef database update", StringComparison.OrdinalIgnoreCase)
      };

    var episodeId = traceStore.AddEpisode(
      sessionId,
      "shell",
      JsonSerializer.Serialize(new
      {
        command,
        exit_code = exitCode,
        output = outputPreview,
        shell = parsed
      }),
      BuildShellSummary(command, shellResult));

    if (shellResult is null)
    {
      return;
    }

    var stager = new AttemptStager(connection);
    stager.Stage(sessionId, episodeId, shellResult);

    if (shellResult.DotnetCommand == "ef" && shellResult.Ef?.MigrationAdded is { Length: > 0 } migrationId)
    {
      graphStore.UpsertNode(new NodeUpsert("migration", migrationId, $"mig:pending:{migrationId}"));
    }
  }

  private static string? TryBuildPostToolUseFallbackBrief(
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    if (SessionHasBriefInjected(connection, sessionId))
    {
      return null;
    }

    var taskId = ReadSessionTaskId(connection, sessionId);
    if (!taskId.HasValue)
    {
      var branch = GitInfo.CurrentBranch(repoRoot);
      taskId = traceStore.FindActiveTaskId(repoRoot, branch);
      if (taskId.HasValue)
      {
        traceStore.SetSessionTask(sessionId, taskId.Value);
      }
    }

    if (!taskId.HasValue)
    {
      return null;
    }

    var brief = BriefBuilder.RenderForTask(connection, taskId.Value);
    if (string.IsNullOrWhiteSpace(brief))
    {
      return null;
    }

    traceStore.MarkBriefInjected(sessionId, PostToolUseEvent);
    var response = new HookAdditionalContextResponse(
      brief,
      new Dictionary<string, string>(StringComparer.Ordinal));
    return JsonSerializer.Serialize(response, HookRunnerJsonContext.Default.HookAdditionalContextResponse);
  }

  private static long? EnsureSessionTask(
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var taskId = ReadSessionTaskId(connection, sessionId);
    if (taskId.HasValue)
    {
      return taskId;
    }

    var branch = GitInfo.CurrentBranch(repoRoot);
    taskId = traceStore.FindActiveTaskId(repoRoot, branch);
    if (taskId.HasValue)
    {
      traceStore.SetSessionTask(sessionId, taskId.Value);
    }

    return taskId;
  }

  private static async Task<HookPayloadEnvelope> ReadPayloadAsync(Stream stdin, CancellationToken cancellationToken)
  {
    using var reader = new StreamReader(stdin, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
    var json = await reader.ReadToEndAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(json))
    {
      return new HookPayloadEnvelope();
    }

    var payload = JsonSerializer.Deserialize(json, HookRunnerJsonContext.Default.HookPayloadEnvelope)
      ?? new HookPayloadEnvelope();
    payload.SetRawPayloadJson(json);
    return payload;
  }

  private static async Task WriteResponseAsync(Stream stdout, string responseJson, CancellationToken cancellationToken)
  {
    await using var writer = new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
    await writer.WriteAsync(responseJson.AsMemory(), cancellationToken);
    await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
    await writer.FlushAsync(cancellationToken);
  }

  private static string ResolveRepoRoot(HookPayloadEnvelope payload)
  {
    var workspaceRoot = payload.WorkspaceRoots?
      .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    if (!string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Path.GetFullPath(workspaceRoot);
    }

    return FindGitRootFromCwd() ?? Directory.GetCurrentDirectory();
  }

  private static string ResolveSessionId(HookPayloadEnvelope payload)
  {
    var sessionId = payload.GetFirstString("session_id", "conversation_id", "generation_id");
    return string.IsNullOrWhiteSpace(sessionId)
      ? $"generated-{Guid.NewGuid():N}"
      : sessionId;
  }

  private static string NormalizeEventName(string? eventName) =>
    string.IsNullOrWhiteSpace(eventName) ? "unknown" : eventName.Trim();

  private static bool IsBeforeSubmitPrompt(string eventName) =>
    string.Equals(eventName, BeforeSubmitPromptEvent, StringComparison.Ordinal);

  private static string? FindGitRootFromCwd()
  {
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
      var gitPath = Path.Combine(directory.FullName, ".git");
      if (Directory.Exists(gitPath) || File.Exists(gitPath))
      {
        return directory.FullName;
      }

      directory = directory.Parent;
    }

    return null;
  }

  private static IReadOnlyList<HookEdit> ReadEdits(HookPayloadEnvelope payload)
  {
    if (payload.Extra is null)
    {
      return [];
    }

    if (!TryGetExtra(payload.Extra, "edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    var edits = new List<HookEdit>();
    foreach (var item in editsElement.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      var oldString = TryGetString(item, "old_string", "oldString") ?? string.Empty;
      var newString = TryGetString(item, "new_string", "newString") ?? string.Empty;
      edits.Add(new HookEdit(oldString, newString));
    }

    return edits;
  }

  private static bool TryGetExtra(IReadOnlyDictionary<string, JsonElement> map, string key, out JsonElement value)
  {
    if (map.TryGetValue(key, out value))
    {
      return true;
    }

    return map.TryGetValue(ToCamelCaseLocal(key), out value);
  }

  private static string? TryGetString(JsonElement element, params string[] keys)
  {
    foreach (var key in keys)
    {
      if (!element.TryGetProperty(key, out var value))
      {
        continue;
      }

      if (value.ValueKind == JsonValueKind.String)
      {
        return value.GetString();
      }
    }

    return null;
  }

  private static string? TryGetString(JsonElement? element, params string[] keys)
  {
    if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    return TryGetString(element.Value, keys);
  }

  private static string ToRepoRelativePath(string repoRoot, string path) =>
    Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

  private static string? ToRepoRelativePathSafe(string repoRoot, string? path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return null;
    }

    var fullPath = Path.IsPathRooted(path)
      ? Path.GetFullPath(path)
      : Path.GetFullPath(Path.Combine(repoRoot, path));
    return ToRepoRelativePath(repoRoot, fullPath);
  }

  private static JsonElement? ReadToolInput(HookPayloadEnvelope payload)
  {
    if (payload.Extra is null)
    {
      return null;
    }

    if (TryGetExtra(payload.Extra, "tool_input", out var input))
    {
      return input;
    }

    if (TryGetExtra(payload.Extra, "input", out input))
    {
      return input;
    }

    return null;
  }

  private static IReadOnlyList<GrepMatch> ParseGrepMatches(string output, string repoRoot)
  {
    var matches = new List<GrepMatch>();
    using var reader = new StringReader(output);
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
      var firstColon = line.IndexOf(':');
      if (firstColon <= 0)
      {
        continue;
      }

      var secondColon = line.IndexOf(':', firstColon + 1);
      if (secondColon <= firstColon + 1)
      {
        continue;
      }

      var path = line[..firstColon];
      var lineNumberText = line[(firstColon + 1)..secondColon];
      if (!int.TryParse(lineNumberText, out var lineNumber))
      {
        continue;
      }

      var relativePath = ToRepoRelativePathSafe(repoRoot, path);
      if (string.IsNullOrWhiteSpace(relativePath))
      {
        continue;
      }

      matches.Add(new GrepMatch(relativePath, lineNumber));
    }

    return matches;
  }

  private static int? TryGetInt32(JsonElement? element, params string[] keys)
  {
    if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    foreach (var key in keys)
    {
      if (!element.Value.TryGetProperty(key, out var value))
      {
        continue;
      }

      if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
      {
        return intValue;
      }

      if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
      {
        return intValue;
      }
    }

    return null;
  }

  private static int? TryGetInt32(IReadOnlyDictionary<string, JsonElement>? map, params string[] keys)
  {
    if (map is null)
    {
      return null;
    }

    foreach (var key in keys)
    {
      if (map.TryGetValue(key, out var value))
      {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
          return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
          return intValue;
        }
      }

      var camelKey = ToCamelCaseLocal(key);
      if (!string.Equals(camelKey, key, StringComparison.Ordinal) && map.TryGetValue(camelKey, out value))
      {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
          return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
          return intValue;
        }
      }
    }

    return null;
  }

  private static string BuildShellSummary(string command, ShellResult? shellResult)
  {
    if (shellResult is null)
    {
      return command.Length == 0 ? "shell" : command;
    }

    if (shellResult.Tests is not null)
    {
      var status = shellResult.Tests.Failed > 0 ? "FAIL" : "PASS";
      var failedName = shellResult.Tests.FailedNames.FirstOrDefault();
      var suffix = failedName is null ? string.Empty : $" ({failedName} failed)";
      return $"dotnet {shellResult.DotnetCommand}: {shellResult.Tests.Passed}/{shellResult.Tests.Passed + shellResult.Tests.Failed + shellResult.Tests.Skipped} {status}{suffix}";
    }

    if (shellResult.Errors.Count > 0)
    {
      return $"dotnet {shellResult.DotnetCommand}: {shellResult.Errors.Count} compile errors";
    }

    if (shellResult.Exception is not null)
    {
      return $"dotnet {shellResult.DotnetCommand}: {shellResult.Exception.Type}";
    }

    return $"dotnet {shellResult.DotnetCommand}: {(shellResult.Failed ? "failed" : "ok")}";
  }

  private static string BuildResponseSummary(string responseText)
  {
    if (string.IsNullOrWhiteSpace(responseText))
    {
      return "response";
    }

    var compact = responseText
      .Replace("\r\n", " ", StringComparison.Ordinal)
      .Replace('\n', ' ')
      .Trim();
    return compact.Length <= 120 ? compact : compact[..120];
  }

  private static bool SessionHasBriefInjected(Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT brief_injected_via FROM session WHERE id = $id LIMIT 1;";
    command.Parameters.AddWithValue("$id", sessionId);
    var value = command.ExecuteScalar() as string;
    return !string.IsNullOrWhiteSpace(value);
  }

  private static long? ReadSessionTaskId(Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT task_id FROM session WHERE id = $id LIMIT 1;";
    command.Parameters.AddWithValue("$id", sessionId);
    var value = command.ExecuteScalar();
    return value is long taskId ? taskId : null;
  }

  private static void TouchFileNode(GraphStore graphStore, Microsoft.Data.Sqlite.SqliteConnection connection, string relativePath, long episodeId)
  {
    var qualifiedName = $"file:{relativePath}";
    graphStore.UpsertNode(new NodeUpsert("file", relativePath, qualifiedName));
    var nodeId = graphStore.NodeIdByQualifiedName(qualifiedName);
    if (nodeId.HasValue)
    {
      InsertTouch(connection, episodeId, nodeId.Value, "edited");
    }
  }

  private static void TouchFileNodeWithHow(
    GraphStore graphStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string relativePath,
    long episodeId,
    string how)
  {
    var qualifiedName = $"file:{relativePath}";
    graphStore.UpsertNode(new NodeUpsert("file", relativePath, qualifiedName));
    var nodeId = graphStore.NodeIdByQualifiedName(qualifiedName);
    if (nodeId.HasValue)
    {
      InsertTouch(connection, episodeId, nodeId.Value, how);
    }
  }

  private static void InsertTouch(Microsoft.Data.Sqlite.SqliteConnection connection, long episodeId, long nodeId, string how)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT OR IGNORE INTO episode_touch(episode_id, node_id, how)
      VALUES($episode_id, $node_id, $how);
      """;
    command.Parameters.AddWithValue("$episode_id", episodeId);
    command.Parameters.AddWithValue("$node_id", nodeId);
    command.Parameters.AddWithValue("$how", how);
    command.ExecuteNonQuery();
  }

  private static int GetLineNumberAtOffset(string text, int offset)
  {
    if (offset <= 0)
    {
      return 1;
    }

    if (offset > text.Length)
    {
      offset = text.Length;
    }

    var line = 1;
    for (var i = 0; i < offset; i++)
    {
      if (text[i] == '\n')
      {
        line++;
      }
    }

    return line;
  }

  private static string Truncate(string value, int maxChars) =>
    value.Length <= maxChars ? value : value[..maxChars];

  private static string ToCamelCaseLocal(string value)
  {
    if (!value.Contains('_', StringComparison.Ordinal))
    {
      return value;
    }

    var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
      return value;
    }

    var builder = new StringBuilder(parts[0]);
    for (var i = 1; i < parts.Length; i++)
    {
      if (parts[i].Length == 0)
      {
        continue;
      }

      builder.Append(char.ToUpperInvariant(parts[i][0]));
      if (parts[i].Length > 1)
      {
        builder.Append(parts[i][1..]);
      }
    }

    return builder.ToString();
  }

  private static int RunCheckpoint(
    HookPayloadEnvelope payload,
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId,
    long? episodeId)
  {
    var taskId = EnsureSessionTask(traceStore, connection, repoRoot, sessionId);
    if (!taskId.HasValue)
    {
      return 0;
    }

    var transcriptPath = payload.GetFirstString("transcript_path");
    if (string.IsNullOrWhiteSpace(transcriptPath))
    {
      return 0;
    }

    var fullTranscriptPath = Path.IsPathRooted(transcriptPath)
      ? transcriptPath
      : Path.GetFullPath(Path.Combine(repoRoot, transcriptPath));
    if (!File.Exists(fullTranscriptPath))
    {
      return 0;
    }

    var transcriptOffsetKey = $"{TranscriptOffsetMetaPrefix}{payload.ConversationId ?? sessionId}";
    var previousOffset = ReadMetaLong(connection, transcriptOffsetKey);
    var transcriptText = File.ReadAllText(fullTranscriptPath);
    var boundedOffset = (int)Math.Clamp(previousOffset, 0, transcriptText.Length);
    var deltaText = transcriptText[boundedOffset..];
    var nextOffset = transcriptText.Length;
    var truncated = false;
    if (deltaText.Length > CheckpointTailChars)
    {
      deltaText = deltaText[^CheckpointTailChars..];
      truncated = true;
    }

    if (string.IsNullOrWhiteSpace(deltaText))
    {
      WriteMeta(connection, transcriptOffsetKey, nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
      return 0;
    }

    var resolver = new SymbolResolver(connection);
    var classifier = new ClaimClassifier(new SymbolLinker(resolver), new SentenceSplitter());
    var claims = classifier.Classify(deltaText, source: "transcript");
    var writer = new ClaimWriter(connection);
    var inserted = writer.WriteClaims(taskId.Value, sessionId, episodeId, claims);

    WriteMeta(connection, transcriptOffsetKey, nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (truncated)
    {
      HookLog.Append(WaypointPaths.ForRepo(repoRoot).LogPath, "checkpoint", new { session = sessionId, truncated = true });
    }

    return inserted;
  }

  private static (long DecisionAndConstraintCount, long OpenItemCount) CountCheckpointClaims(
    Microsoft.Data.Sqlite.SqliteConnection connection,
    long? taskId)
  {
    if (!taskId.HasValue)
    {
      return (0, 0);
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT
        SUM(CASE WHEN kind IN ('decision', 'constraint') AND status = 'current' THEN 1 ELSE 0 END),
        SUM(CASE WHEN kind = 'open_item' AND status = 'current' THEN 1 ELSE 0 END)
      FROM claim
      WHERE task_id = $task_id;
      """;
    command.Parameters.AddWithValue("$task_id", taskId.Value);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return (0, 0);
    }

    var decisionsAndConstraints = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
    var openItems = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
    return (decisionsAndConstraints, openItems);
  }

  private static bool HasCsEditSincePreviousStop(Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT COUNT(*)
      FROM episode
      WHERE session_id = $session_id
        AND kind = 'edit'
        AND id > COALESCE(
          (SELECT MAX(id) FROM episode WHERE session_id = $session_id AND kind = 'stop'),
          0)
        AND lower(COALESCE(json_extract(payload_json, '$.file'), '')) LIKE '%.cs';
      """;
    command.Parameters.AddWithValue("$session_id", sessionId);
    var value = command.ExecuteScalar();
    return value is long count && count > 0;
  }

  private static void UpdateSessionTokenCounters(
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string sessionId,
    HookPayloadEnvelope payload)
  {
    var usage = TryGetObject(payload.Extra, "usage");
    var inputTokens = TryGetInt32(payload.Extra, "input_tokens", "inputTokens")
      ?? TryGetInt32(usage, "input_tokens", "inputTokens", "prompt_tokens", "promptTokens");
    var outputTokens = TryGetInt32(payload.Extra, "output_tokens", "outputTokens")
      ?? TryGetInt32(usage, "output_tokens", "outputTokens", "completion_tokens", "completionTokens");
    var cacheReadTokens = TryGetInt32(payload.Extra, "cache_read_tokens", "cacheReadTokens")
      ?? TryGetInt32(usage, "cache_read_tokens", "cacheReadTokens");

    if (!inputTokens.HasValue && !outputTokens.HasValue && !cacheReadTokens.HasValue)
    {
      return;
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
      UPDATE session
      SET input_tokens = COALESCE($input_tokens, input_tokens),
          output_tokens = COALESCE($output_tokens, output_tokens),
          cache_read_tokens = COALESCE($cache_read_tokens, cache_read_tokens)
      WHERE id = $id;
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.Parameters.AddWithValue("$input_tokens", (object?)inputTokens ?? DBNull.Value);
    command.Parameters.AddWithValue("$output_tokens", (object?)outputTokens ?? DBNull.Value);
    command.Parameters.AddWithValue("$cache_read_tokens", (object?)cacheReadTokens ?? DBNull.Value);
    command.ExecuteNonQuery();
  }

  private static void WriteResumeFile(
    TraceStore traceStore,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    string repoRoot,
    string sessionId)
  {
    var taskId = EnsureSessionTask(traceStore, connection, repoRoot, sessionId);
    var resumeMarkdown = taskId.HasValue
      ? BriefBuilder.RenderForTask(connection, taskId.Value)
      : null;

    if (string.IsNullOrWhiteSpace(resumeMarkdown))
    {
      resumeMarkdown = $"No active Waypoint task for {repoRoot}@{GitInfo.CurrentBranch(repoRoot) ?? "(detached)"}";
    }

    var resumeDir = Path.Combine(repoRoot, ".cursor", "waypoint");
    Directory.CreateDirectory(resumeDir);
    var resumePath = Path.Combine(resumeDir, "RESUME.md");
    File.WriteAllText(resumePath, resumeMarkdown + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
  }

  private static long ReadMetaLong(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
    command.Parameters.AddWithValue("$key", key);
    var value = command.ExecuteScalar() as string;
    if (!long.TryParse(value, out var parsed))
    {
      return 0;
    }

    return parsed;
  }

  private static void WriteMeta(Microsoft.Data.Sqlite.SqliteConnection connection, string key, string value)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO meta(key, value)
      VALUES($key, $value)
      ON CONFLICT(key) DO UPDATE SET value = excluded.value;
      """;
    command.Parameters.AddWithValue("$key", key);
    command.Parameters.AddWithValue("$value", value);
    command.ExecuteNonQuery();
  }

  private static JsonElement? TryGetObject(IReadOnlyDictionary<string, JsonElement>? map, params string[] keys)
  {
    if (map is null)
    {
      return null;
    }

    foreach (var key in keys)
    {
      if (map.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object)
      {
        return value;
      }

      var camelKey = ToCamelCaseLocal(key);
      if (!string.Equals(camelKey, key, StringComparison.Ordinal)
          && map.TryGetValue(camelKey, out value)
          && value.ValueKind == JsonValueKind.Object)
      {
        return value;
      }
    }

    return null;
  }
}

public sealed class HookPayloadEnvelope
{
  [JsonPropertyName("conversation_id")]
  public string? ConversationId { get; set; }

  [JsonPropertyName("generation_id")]
  public string? GenerationId { get; set; }

  [JsonPropertyName("session_id")]
  public string? SessionId { get; set; }

  [JsonPropertyName("model")]
  public string? Model { get; set; }

  [JsonPropertyName("hook_event_name")]
  public string? HookEventName { get; set; }

  [JsonPropertyName("workspace_roots")]
  public string[]? WorkspaceRoots { get; set; }

  [JsonPropertyName("transcript_path")]
  public string? TranscriptPath { get; set; }

  [JsonPropertyName("cursor_version")]
  public string? CursorVersion { get; set; }

  [JsonExtensionData]
  public Dictionary<string, JsonElement>? Extra { get; set; }

  private string? RawPayloadJson { get; set; }

  public string? GetFirstString(params string[] keys)
  {
    foreach (var key in keys)
    {
      var normalized = key?.Trim();
      if (string.IsNullOrWhiteSpace(normalized))
      {
        continue;
      }

      var directValue = normalized switch
      {
        "conversation_id" => ConversationId,
        "generation_id" => GenerationId,
        "session_id" => SessionId,
        "model" => Model,
        "hook_event_name" => HookEventName,
        "transcript_path" => TranscriptPath,
        "cursor_version" => CursorVersion,
        _ => null
      };

      if (!string.IsNullOrWhiteSpace(directValue))
      {
        return directValue;
      }

      if (Extra is null)
      {
        continue;
      }

      if (TryReadString(Extra, normalized, out var value) && !string.IsNullOrWhiteSpace(value))
      {
        return value;
      }
    }

    return null;
  }

  public string? GetRawPayloadJson() => RawPayloadJson;

  public void SetRawPayloadJson(string json) => RawPayloadJson = json;

  private static bool TryReadString(IReadOnlyDictionary<string, JsonElement> map, string key, out string? value)
  {
    value = null;
    if (map.TryGetValue(key, out var direct) && direct.ValueKind == JsonValueKind.String)
    {
      value = direct.GetString();
      return true;
    }

    // Allow either snake_case or camelCase names when reading event-specific fields.
    if (map.TryGetValue(ToCamelCase(key), out var camel) && camel.ValueKind == JsonValueKind.String)
    {
      value = camel.GetString();
      return true;
    }

    return false;
  }

  private static string ToCamelCase(string value)
  {
    if (!value.Contains('_', StringComparison.Ordinal))
    {
      return value;
    }

    var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
      return value;
    }

    var builder = new StringBuilder(parts[0]);
    for (var i = 1; i < parts.Length; i++)
    {
      if (parts[i].Length == 0)
      {
        continue;
      }

      builder.Append(char.ToUpperInvariant(parts[i][0]));
      if (parts[i].Length > 1)
      {
        builder.Append(parts[i][1..]);
      }
    }

    return builder.ToString();
  }
}

public sealed record HookContinueResponse([property: JsonPropertyName("continue")] bool Continue);

public sealed record HookAdditionalContextResponse(
  [property: JsonPropertyName("additional_context")] string AdditionalContext,
  [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string> Env);

public sealed record HookUserMessageResponse(
  [property: JsonPropertyName("user_message")] string UserMessage);

[JsonSerializable(typeof(HookPayloadEnvelope))]
[JsonSerializable(typeof(HookContinueResponse))]
[JsonSerializable(typeof(HookAdditionalContextResponse))]
[JsonSerializable(typeof(HookUserMessageResponse))]
internal sealed partial class HookRunnerJsonContext : JsonSerializerContext;

internal sealed record HookEdit(string OldString, string NewString);

internal sealed record GrepMatch(string Path, int Line);

internal sealed class DefaultBackgroundProcessLauncher : IBackgroundProcessLauncher
{
  public static DefaultBackgroundProcessLauncher Instance { get; } = new();

  public void LaunchIndex(string repoRoot)
  {
    var processPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(processPath))
    {
      return;
    }

    if (OperatingSystem.IsWindows())
    {
      var startInfo = new System.Diagnostics.ProcessStartInfo
      {
        FileName = processPath,
        UseShellExecute = true,
        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        Arguments = $"index --background --repo \"{repoRoot}\" --quiet"
      };
      _ = System.Diagnostics.Process.Start(startInfo);
      return;
    }

    var escapedBinary = processPath.Replace("\"", "\\\"", StringComparison.Ordinal);
    var escapedRepo = repoRoot.Replace("\"", "\\\"", StringComparison.Ordinal);
    var shellCommand = $"\"{escapedBinary}\" index --background --repo \"{escapedRepo}\" --quiet >/dev/null 2>&1 &";
    var shellStartInfo = new System.Diagnostics.ProcessStartInfo
    {
      FileName = "/bin/sh",
      UseShellExecute = false
    };
    shellStartInfo.ArgumentList.Add("-c");
    shellStartInfo.ArgumentList.Add(shellCommand);
    _ = System.Diagnostics.Process.Start(shellStartInfo);
  }
}
