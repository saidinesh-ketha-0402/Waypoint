using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Queries;

namespace Waypoint.Viz;

public enum ExportFormat
{
  Html,
  Json
}

public sealed record ExportRequest(
  long TaskId,
  string OutputDirectory,
  ExportFormat Format = ExportFormat.Html,
  bool IncludeUntouchedEndpoints = false);

public sealed record ExportResult(string MainOutputPath, string JsonOutputPath, string? HtmlOutputPath);

public sealed class TaskGraphExporter(SqliteConnection connection)
{
  private const string HtmlTemplateToken = "__WAYPOINT_DATA__";
  private const string CytoscapeTemplateToken = "__WAYPOINT_CYTOSCAPE__";
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public ExportResult Export(ExportRequest request)
  {
    if (string.IsNullOrWhiteSpace(request.OutputDirectory))
    {
      throw new ArgumentException("Output directory is required.", nameof(request));
    }

    var graph = new TaskGraphQuery(_connection).Run(request.TaskId, request.IncludeUntouchedEndpoints);
    var payload = BuildPayload(graph);
    var json = JsonSerializer.Serialize(payload, ExportJsonContext.Default.ExportPayload);

    var outputDirectory = Path.GetFullPath(request.OutputDirectory);
    Directory.CreateDirectory(outputDirectory);
    var jsonPath = Path.Combine(outputDirectory, "graph.json");
    File.WriteAllText(jsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    if (request.Format == ExportFormat.Json)
    {
      return new ExportResult(Path.GetFullPath(jsonPath), Path.GetFullPath(jsonPath), null);
    }

    var htmlPath = Path.Combine(outputDirectory, "graph.html");
    var html = BuildHtml(json);
    File.WriteAllText(htmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return new ExportResult(Path.GetFullPath(htmlPath), Path.GetFullPath(jsonPath), Path.GetFullPath(htmlPath));
  }

  private static ExportPayload BuildPayload(TaskGraphResult graph)
  {
    var nodes = new List<ExportNode>(graph.Nodes.Count + graph.Claims.Count);
    nodes.AddRange(graph.Nodes.Select(node => new ExportNode(
      $"n{node.NodeId}",
      node.Label,
      node.Kind,
      node.Project,
      node.Touched,
      node.Edits,
      node.Sessions,
      node.QualifiedName,
      null,
      null,
      null,
      null,
      null)));

    nodes.AddRange(graph.Claims.Select(claim => new ExportNode(
      $"c{claim.ClaimId}",
      BuildClaimLabel(claim.Text),
      "claim",
      null,
      true,
      0,
      claim.SessionId is null ? [] : [claim.SessionId],
      null,
      claim.Kind,
      claim.Status,
      claim.SessionId,
      claim.Confidence,
      claim.Text)));

    var edges = new List<ExportEdge>(graph.Edges.Count + graph.ClaimEdges.Count);
    edges.AddRange(graph.Edges.Select(edge => new ExportEdge($"n{edge.SourceNodeId}", $"n{edge.TargetNodeId}", edge.Kind)));
    edges.AddRange(graph.ClaimEdges.Select(edge => new ExportEdge($"c{edge.SourceClaimId}", edge.TargetId, edge.Kind)));

    return new ExportPayload(
      new ExportTask(graph.Task.TaskId, graph.Task.Title, graph.Task.SessionCount),
      nodes,
      edges,
      graph.GeneratedAtUtc.ToString("O"));
  }

  private static string BuildClaimLabel(string text)
  {
    const int max = 84;
    var clean = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
    if (clean.Length <= max)
    {
      return clean;
    }

    return clean[..max] + "...";
  }

  private static string BuildHtml(string json)
  {
    var template = ReadEmbeddedText("Waypoint.Viz.Assets.graph-template.html");
    var cytoscapeBundle = ReadEmbeddedText("Waypoint.Viz.Assets.cytoscape.min.js");
    return template
      .Replace(CytoscapeTemplateToken, cytoscapeBundle, StringComparison.Ordinal)
      .Replace(HtmlTemplateToken, json, StringComparison.Ordinal);
  }

  private static string ReadEmbeddedText(string resourceName)
  {
    var assembly = typeof(TaskGraphExporter).Assembly;
    using var stream = assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return reader.ReadToEnd();
  }
}

public sealed record ExportTask(
  [property: JsonPropertyName("id")] long Id,
  [property: JsonPropertyName("title")] string Title,
  [property: JsonPropertyName("sessions")] int Sessions);

public sealed record ExportNode(
  [property: JsonPropertyName("id")] string Id,
  [property: JsonPropertyName("label")] string Label,
  [property: JsonPropertyName("kind")] string Kind,
  [property: JsonPropertyName("project")] string? Project,
  [property: JsonPropertyName("touched")] bool Touched,
  [property: JsonPropertyName("edits")] int Edits,
  [property: JsonPropertyName("sessions")] IReadOnlyList<string> Sessions,
  [property: JsonPropertyName("qualified_name")] string? QualifiedName,
  [property: JsonPropertyName("claim_kind")] string? ClaimKind,
  [property: JsonPropertyName("status")] string? Status,
  [property: JsonPropertyName("session")] string? Session,
  [property: JsonPropertyName("confidence")] double? Confidence,
  [property: JsonPropertyName("text")] string? Text);

public sealed record ExportEdge(
  [property: JsonPropertyName("source")] string Source,
  [property: JsonPropertyName("target")] string Target,
  [property: JsonPropertyName("kind")] string Kind);

public sealed record ExportPayload(
  [property: JsonPropertyName("task")] ExportTask Task,
  [property: JsonPropertyName("nodes")] IReadOnlyList<ExportNode> Nodes,
  [property: JsonPropertyName("edges")] IReadOnlyList<ExportEdge> Edges,
  [property: JsonPropertyName("generated_at")] string GeneratedAt);

[JsonSerializable(typeof(ExportPayload))]
internal sealed partial class ExportJsonContext : JsonSerializerContext;
