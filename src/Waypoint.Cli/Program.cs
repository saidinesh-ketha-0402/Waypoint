using Waypoint.Cli;
using Waypoint.Core.Indexing;
using Waypoint.Mcp;
using Waypoint.Viz;

if (args is ["hook", "ping"])
{
  return await HookPingCommand.ExecuteAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h" or "-?"))
{
  PrintRootHelp();
  return 0;
}

var command = args[0];
if (string.Equals(command, "index", StringComparison.Ordinal))
{
  return await HandleIndexAsync(args[1..]);
}

if (string.Equals(command, "hook", StringComparison.Ordinal))
{
  return await HandleHookAsync(args[1..]);
}

if (string.Equals(command, "brief", StringComparison.Ordinal))
{
  return await HandleBriefAsync(args[1..]);
}

if (string.Equals(command, "serve", StringComparison.Ordinal))
{
  return await HandleServeAsync(args[1..]);
}

if (string.Equals(command, "export", StringComparison.Ordinal))
{
  return await HandleExportAsync(args[1..]);
}

if (command is "bench")
{
  Console.Error.WriteLine("not implemented");
  return 2;
}

Console.Error.WriteLine($"Unknown command: {command}");
PrintRootHelp();
return 1;

static async Task<int> HandleIndexAsync(string[] args)
{
  string? repo = null;
  string? solution = null;
  var projects = new List<string>();
  var modeText = "msbuild";
  var force = false;
  var background = false;
  var quiet = false;

  for (var i = 0; i < args.Length; i++)
  {
    var token = args[i];
    switch (token)
    {
      case "--help":
      case "-h":
      case "-?":
        PrintIndexHelp();
        return 0;
      case "--repo":
        if (!TryReadValue(args, ref i, out repo))
        {
          return Error("Missing value for --repo.");
        }

        break;
      case "--solution":
        if (!TryReadValue(args, ref i, out solution))
        {
          return Error("Missing value for --solution.");
        }

        break;
      case "--project":
        if (!TryReadValue(args, ref i, out var project))
        {
          return Error("Missing value for --project.");
        }

        projects.Add(project!);
        break;
      case "--mode":
        if (!TryReadValue(args, ref i, out modeText))
        {
          return Error("Missing value for --mode.");
        }

        break;
      case "--force":
        force = true;
        break;
      case "--background":
        background = true;
        break;
      case "--quiet":
        quiet = true;
        break;
      default:
        return Error($"Unknown index option: {token}");
    }
  }

  if (!TryParseMode(modeText!, out var mode))
  {
    return Error("Mode must be one of: msbuild, compilation, syntax, auto.");
  }

  return await IndexCommand.ExecuteAsync(
    repo,
    solution,
    projects,
    mode,
    force,
    background,
    quiet,
    CancellationToken.None);
}

static bool TryReadValue(string[] args, ref int index, out string? value)
{
  if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
  {
    value = null;
    return false;
  }

  index++;
  value = args[index];
  return true;
}

static int Error(string message)
{
  Console.Error.WriteLine(message);
  return 1;
}

static async Task<int> HandleHookAsync(string[] args)
{
  if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h" or "-?"))
  {
    PrintHookHelp();
    return 0;
  }

  var eventName = args[0];
  var injectBriefOnce = false;

  for (var i = 1; i < args.Length; i++)
  {
    var token = args[i];
    switch (token)
    {
      case "--inject-brief-once":
        injectBriefOnce = true;
        break;
      default:
        return Error($"Unknown hook option: {token}");
    }
  }

  return await HookRunner.RunAsync(
    eventName,
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    new HookRunnerOptions(injectBriefOnce),
    CancellationToken.None);
}

static async Task<int> HandleBriefAsync(string[] args)
{
  string? repo = null;
  long? task = null;

  for (var i = 0; i < args.Length; i++)
  {
    var token = args[i];
    switch (token)
    {
      case "--help":
      case "-h":
      case "-?":
        PrintBriefHelp();
        return 0;
      case "--repo":
        if (!TryReadValue(args, ref i, out repo))
        {
          return Error("Missing value for --repo.");
        }

        break;
      case "--task":
        if (!TryReadValue(args, ref i, out var taskText))
        {
          return Error("Missing value for --task.");
        }

        if (!long.TryParse(taskText, out var parsedTaskId) || parsedTaskId <= 0)
        {
          return Error("--task must be a positive numeric task id.");
        }

        task = parsedTaskId;
        break;
      default:
        return Error($"Unknown brief option: {token}");
    }
  }

  return await BriefCommand.ExecuteAsync(repo, task, CancellationToken.None);
}

static async Task<int> HandleServeAsync(string[] args)
{
  string? repo = null;
  for (var i = 0; i < args.Length; i++)
  {
    var token = args[i];
    switch (token)
    {
      case "--help":
      case "-h":
      case "-?":
        PrintServeHelp();
        return 0;
      case "--repo":
        if (!TryReadValue(args, ref i, out repo))
        {
          return Error("Missing value for --repo.");
        }

        break;
      default:
        return Error($"Unknown serve option: {token}");
    }
  }

  var repoRoot = ResolveRepoRoot(repo);
  await WaypointMcpServer.RunAsync(repoRoot, CancellationToken.None);
  return 0;
}

static async Task<int> HandleExportAsync(string[] args)
{
  string? repo = null;
  long? taskId = null;
  string? outputDirectory = null;
  var format = ExportFormat.Html;
  var includeUntouchedEndpoints = false;

  for (var i = 0; i < args.Length; i++)
  {
    var token = args[i];
    switch (token)
    {
      case "--help":
      case "-h":
      case "-?":
        PrintExportHelp();
        return 0;
      case "--repo":
        if (!TryReadValue(args, ref i, out repo))
        {
          return Error("Missing value for --repo.");
        }

        break;
      case "--task":
        if (!TryReadValue(args, ref i, out var taskText))
        {
          return Error("Missing value for --task.");
        }

        if (!long.TryParse(taskText, out var parsedTaskId) || parsedTaskId <= 0)
        {
          return Error("--task must be a positive numeric task id.");
        }

        taskId = parsedTaskId;
        break;
      case "--out":
        if (!TryReadValue(args, ref i, out outputDirectory))
        {
          return Error("Missing value for --out.");
        }

        break;
      case "--format":
        if (!TryReadValue(args, ref i, out var formatText))
        {
          return Error("Missing value for --format.");
        }

        if (string.Equals(formatText, "html", StringComparison.OrdinalIgnoreCase))
        {
          format = ExportFormat.Html;
        }
        else if (string.Equals(formatText, "json", StringComparison.OrdinalIgnoreCase))
        {
          format = ExportFormat.Json;
        }
        else
        {
          return Error("--format must be one of: html, json.");
        }

        break;
      case "--include-untouched-endpoints":
        includeUntouchedEndpoints = true;
        break;
      default:
        return Error($"Unknown export option: {token}");
    }
  }

  return await ExportCommand.ExecuteAsync(
    repo,
    taskId,
    outputDirectory,
    format,
    includeUntouchedEndpoints,
    CancellationToken.None);
}

static bool TryParseMode(string text, out IndexMode mode)
{
  switch (text.ToLowerInvariant())
  {
    case "msbuild":
      mode = IndexMode.MsBuild;
      return true;
    case "compilation":
      mode = IndexMode.Compilation;
      return true;
    case "syntax":
      mode = IndexMode.Syntax;
      return true;
    case "auto":
      mode = IndexMode.Auto;
      return true;
    default:
      mode = default;
      return false;
  }
}

static void PrintRootHelp()
{
  Console.WriteLine("Description:");
  Console.WriteLine("  Waypoint CLI");
  Console.WriteLine();
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint <command> [options]");
  Console.WriteLine();
  Console.WriteLine("Global options:");
  Console.WriteLine("  --repo <path>  Repository root path for commands that support it.");
  Console.WriteLine();
  Console.WriteLine("Commands:");
  Console.WriteLine("  index  Builds or refreshes the Waypoint semantic index.");
  Console.WriteLine("  hook   Handles Cursor hook events.");
  Console.WriteLine("  serve  Runs the Waypoint MCP stdio server.");
  Console.WriteLine("  brief  Prints a Waypoint resume brief.");
  Console.WriteLine("  export Exports task graph as graph.json and graph.html.");
  Console.WriteLine("  bench  Not implemented yet.");
}

static void PrintIndexHelp()
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint index [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --repo <path>         Repository root path.");
  Console.WriteLine("  --solution <path>     Path to a .sln or .slnx file.");
  Console.WriteLine("  --project <name>      Include project by name (repeatable).");
  Console.WriteLine("  --mode <mode>         msbuild | compilation | syntax | auto (default msbuild).");
  Console.WriteLine("  --force               Re-index files even when content hashes match.");
  Console.WriteLine("  --background          Skip if another live background index lock exists.");
  Console.WriteLine("  --quiet               Suppress report output.");
}

static void PrintHookHelp()
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint hook <event> [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --inject-brief-once   Enable one-time fallback brief injection on postToolUse.");
}

static void PrintBriefHelp()
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint brief [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --repo <path>         Repository root path.");
  Console.WriteLine("  --task <id>           Optional task id to render.");
}

static void PrintServeHelp()
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint serve [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --repo <path>         Repository root path.");
}

static void PrintExportHelp()
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  waypoint export [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --repo <path>                   Repository root path.");
  Console.WriteLine("  --task <id>                     Task id to export (default active task).");
  Console.WriteLine("  --out <dir>                     Output directory (default <repo>/.git/waypoint/viz).");
  Console.WriteLine("  --format <html|json>            Export format (default html).");
  Console.WriteLine("  --include-untouched-endpoints   Include untouched endpoint neighbours.");
}

static string ResolveRepoRoot(string? repoRootOverride)
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
