using System.Text;

namespace Waypoint.Core.Queries;

public sealed class MarkdownFormatter
{
  public string RenderImpact(string symbol, ImpactResult impact)
  {
    var title = $"### impact: {symbol} ({impact.Start.Kind}{FormatKindProject(impact.Start.Project)})";
    var lines = new List<string> { title };

    lines.Add("**Implementations / registrations**");
    if (impact.Implementations.Count == 0)
    {
      lines.Add("- none");
    }
    else
    {
      foreach (var item in impact.Implementations)
      {
        var bullet = new StringBuilder();
        bullet.Append("- ");
        bullet.Append(DisplayName(item.Implementation));
        if (!string.IsNullOrWhiteSpace(item.Lifetime))
        {
          bullet.Append(" — ");
          bullet.Append(ToRegistrationCall(item.Lifetime));
        }

        if (!string.IsNullOrWhiteSpace(item.RegistrationSourcePath))
        {
          bullet.Append(" (");
          bullet.Append(item.RegistrationSourcePath);
          bullet.Append(')');
        }

        lines.Add(bullet.ToString());
      }
    }

    lines.Add("**Injected by**");
    AddInjectedBySection(lines, impact);

    if (impact.Callers.Count > 0 || impact.Callees.Count > 0)
    {
      lines.Add("**Callers / callees**");
      foreach (var caller in impact.Callers)
      {
        lines.Add("- caller: " + DisplayName(caller) + FormatProject(caller.Project));
      }

      foreach (var callee in impact.Callees)
      {
        lines.Add("- callee: " + DisplayName(callee) + FormatProject(callee.Project));
      }
    }

    lines.Add("**Endpoints reaching it**");
    if (impact.Endpoints.Count == 0)
    {
      lines.Add("- none");
    }
    else
    {
      foreach (var endpoint in impact.Endpoints)
      {
        lines.Add($"- {DisplayName(endpoint.Endpoint)} → {FormatActionTarget(endpoint.Action)}");
      }
    }

    lines.Add("**Tests**");
    if (impact.Tests.Count == 0)
    {
      lines.Add("- none");
    }
    else
    {
      lines.Add("- " + string.Join(", ", impact.Tests.Select(item => DisplayName(item.Test))));
    }

    return string.Join(Environment.NewLine, lines);
  }

  public string RenderWhy(string symbol, IReadOnlyList<WhyEntry> entries, IReadOnlyDictionary<long, IReadOnlyDictionary<string, int>> sessionOrdinalsByTask)
  {
    if (entries.Count == 0)
    {
      return $"### why: {symbol}{Environment.NewLine}No decisions, constraints, failed attempts, or open items are recorded for this symbol yet.";
    }

    var lines = new List<string> { $"### why: {symbol}" };
    foreach (var entry in entries)
    {
      var tag = SessionTag(sessionOrdinalsByTask, entry.TaskId, entry.SessionId);
      var evidence = string.IsNullOrWhiteSpace(entry.EpisodeSummary)
        ? string.Empty
        : $" — evidence: {entry.EpisodeSummary}";
      lines.Add($"- [{entry.Kind}/{entry.Status}] {entry.Text}{tag} (conf {entry.Confidence:0.00}){evidence}");
    }

    return string.Join(Environment.NewLine, lines);
  }

  public string RenderWhatChanged(WhatChangedResult result, IReadOnlyDictionary<string, int> sessionOrdinals)
  {
    if (result.Changes.Count == 0 && result.Migrations.Count == 0)
    {
      return "### what_changed" + Environment.NewLine + "No edits recorded for this task yet.";
    }

    var lines = new List<string> { "### what_changed" };
    foreach (var group in result.Changes
               .GroupBy(change => string.IsNullOrWhiteSpace(change.Project) ? "(no project)" : change.Project, StringComparer.Ordinal)
               .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
      lines.Add($"**{group.Key}**");
      foreach (var change in group)
      {
        var decisionText = change.DecisionText ?? "no explicit decision captured yet";
        var sessionTag = SessionTag(sessionOrdinals, change.DecisionSessionId);
        lines.Add($"- `{change.Name}` — edited {change.EditCount}x — decision: {decisionText}{sessionTag}");
      }
    }

    if (result.Migrations.Count > 0)
    {
      lines.Add("**Migrations**");
      foreach (var migration in result.Migrations)
      {
        lines.Add("- " + migration);
      }
    }

    return string.Join(Environment.NewLine, lines);
  }

  public string RenderOpenItems(OpenItemsResult result)
  {
    var lines = new List<string>();
    if (!string.IsNullOrWhiteSpace(result.ResolveMessage))
    {
      lines.Add(result.ResolveMessage);
      lines.Add(string.Empty);
    }

    lines.Add("### open_items");
    if (result.Items.Count == 0)
    {
      lines.Add("- none");
      return string.Join(Environment.NewLine, lines);
    }

    for (var i = 0; i < result.Items.Count; i++)
    {
      var item = result.Items[i];
      lines.Add($"{i + 1}. [#{item.Id}] {item.Text}");
    }

    return string.Join(Environment.NewLine, lines);
  }

  public string RenderSearch(string query, IReadOnlyList<SearchHit> hits, IReadOnlyDictionary<long, IReadOnlyDictionary<string, int>> sessionOrdinalsByTask)
  {
    var lines = new List<string> { $"### search: {query}" };
    if (hits.Count == 0)
    {
      lines.Add("- no matches");
      return string.Join(Environment.NewLine, lines);
    }

    foreach (var hit in hits)
    {
      var tag = hit.TaskId.HasValue ? SessionTag(sessionOrdinalsByTask, hit.TaskId.Value, hit.SessionId) : string.Empty;
      lines.Add($"- [{hit.Kind}] {hit.Text}{tag}");
    }

    return string.Join(Environment.NewLine, lines);
  }

  private static void AddNodeSection(ICollection<string> lines, IReadOnlyList<NodeSummary> nodes)
  {
    if (nodes.Count == 0)
    {
      lines.Add("- none");
      return;
    }

    foreach (var node in nodes)
    {
      lines.Add("- " + DisplayName(node) + FormatProject(node.Project));
    }
  }

  private static void AddInjectedBySection(ICollection<string> lines, ImpactResult impact)
  {
    if (impact.Consumers.Count == 0)
    {
      lines.Add("- none");
      return;
    }

    var consumerIds = impact.Consumers.Select(node => node.NodeId).ToHashSet();
    var inbound = impact.ConsumerLinks
      .GroupBy(link => link.Dependency.NodeId)
      .ToDictionary(group => group.Key, group => group.Select(link => link.Consumer).OrderBy(node => node.Name, StringComparer.Ordinal).ToArray());

    var roots = impact.ConsumerLinks
      .Where(link => !consumerIds.Contains(link.Dependency.NodeId))
      .Select(link => link.Consumer)
      .DistinctBy(node => node.NodeId)
      .OrderBy(node => node.Name, StringComparer.Ordinal)
      .ToArray();

    if (roots.Length == 0)
    {
      AddNodeSection(lines, impact.Consumers);
      return;
    }

    foreach (var root in roots)
    {
      if (!inbound.TryGetValue(root.NodeId, out var directConsumers) || directConsumers.Length == 0)
      {
        lines.Add("- " + DisplayName(root) + FormatProject(root.Project));
        continue;
      }

      foreach (var dependent in directConsumers)
      {
        lines.Add($"- {DisplayName(root)}{FormatProject(root.Project)} → {DisplayName(dependent)}{FormatProject(dependent.Project)}");
      }
    }
  }

  private static string DisplayName(NodeSummary node) => node.IsTouched ? $"{node.Name} ★" : node.Name;

  private static string FormatActionTarget(NodeSummary action)
  {
    var qualified = action.QualifiedName;
    if (qualified.StartsWith("M:", StringComparison.Ordinal))
    {
      var withoutPrefix = qualified[2..];
      var callEnd = withoutPrefix.IndexOf('(');
      var signature = callEnd >= 0 ? withoutPrefix[..callEnd] : withoutPrefix;
      var lastDot = signature.LastIndexOf('.');
      if (lastDot > 0)
      {
        var typePath = signature[..lastDot];
        var method = signature[(lastDot + 1)..];
        var typeName = typePath[(typePath.LastIndexOf('.') + 1)..];
        return $"{typeName}.{method}";
      }
    }

    return action.Name;
  }

  private static string ToRegistrationCall(string lifetime) =>
    lifetime switch
    {
      "Scoped" => "AddScoped",
      "Singleton" => "AddSingleton",
      "Transient" => "AddTransient",
      _ => lifetime
    };

  private static string SessionTag(
    IReadOnlyDictionary<long, IReadOnlyDictionary<string, int>> sessionOrdinalsByTask,
    long taskId,
    string? sessionId)
  {
    if (sessionId is null)
    {
      return string.Empty;
    }

    if (!sessionOrdinalsByTask.TryGetValue(taskId, out var taskOrdinals))
    {
      return string.Empty;
    }

    return SessionTag(taskOrdinals, sessionId);
  }

  private static string SessionTag(IReadOnlyDictionary<string, int> sessionOrdinals, string? sessionId)
  {
    if (sessionId is null || !sessionOrdinals.TryGetValue(sessionId, out var ordinal))
    {
      return string.Empty;
    }

    return $" [s{ordinal}]";
  }

  private static string FormatProject(string? project) =>
    string.IsNullOrWhiteSpace(project)
      ? string.Empty
      : $" ({project})";

  private static string FormatKindProject(string? project) =>
    string.IsNullOrWhiteSpace(project)
      ? string.Empty
      : $" · {project}";
}
