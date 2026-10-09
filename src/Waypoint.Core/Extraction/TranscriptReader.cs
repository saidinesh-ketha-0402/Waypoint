using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Extraction;

public sealed class TranscriptReader
{
  public IReadOnlyList<TranscriptBlock> ReadAssistantAndToolBlocks(string transcriptPath)
  {
    if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
    {
      return [];
    }

    var text = File.ReadAllText(transcriptPath, Encoding.UTF8);
    if (string.IsNullOrWhiteSpace(text))
    {
      return [];
    }

    var jsonlBlocks = TryReadJsonLines(text);
    if (jsonlBlocks.Count > 0)
    {
      return jsonlBlocks;
    }

    return ReadPlainTextBlocks(text);
  }

  private static IReadOnlyList<TranscriptBlock> TryReadJsonLines(string text)
  {
    var blocks = new List<TranscriptBlock>();
    using var reader = new StringReader(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
      if (string.IsNullOrWhiteSpace(line))
      {
        continue;
      }

      JsonDocument document;
      try
      {
        document = JsonDocument.Parse(line);
      }
      catch (JsonException)
      {
        return [];
      }

      using (document)
      {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
          continue;
        }

        var role = TryReadString(root, "role", "speaker", "type");
        if (!IsAssistantOrTool(role))
        {
          continue;
        }

        var body = TryReadString(root, "content", "text", "message", "output", "result");
        if (string.IsNullOrWhiteSpace(body))
        {
          continue;
        }

        blocks.Add(new TranscriptBlock(role!, body.Trim()));
      }
    }

    return blocks;
  }

  private static IReadOnlyList<TranscriptBlock> ReadPlainTextBlocks(string text)
  {
    var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    var lines = normalized.Split('\n');
    var blocks = new List<TranscriptBlock>();

    string? currentRole = null;
    var currentBody = new StringBuilder();

    static bool TryReadRoleLine(string line, out string role, out string body)
    {
      var trimmed = line.TrimStart();
      var index = trimmed.IndexOf(':');
      if (index <= 0)
      {
        role = string.Empty;
        body = string.Empty;
        return false;
      }

      role = trimmed[..index].Trim().ToLowerInvariant();
      body = trimmed[(index + 1)..].TrimStart();
      return role is "assistant" or "tool" or "tool_result" or "toolresult";
    }

    void Flush()
    {
      if (!IsAssistantOrTool(currentRole))
      {
        currentRole = null;
        currentBody.Clear();
        return;
      }

      var content = currentBody.ToString().Trim();
      if (content.Length > 0)
      {
        blocks.Add(new TranscriptBlock(currentRole!, content));
      }

      currentRole = null;
      currentBody.Clear();
    }

    foreach (var line in lines)
    {
      if (TryReadRoleLine(line, out var role, out var body))
      {
        Flush();
        currentRole = role;
        currentBody.Append(body);
        continue;
      }

      if (currentRole is null)
      {
        continue;
      }

      if (currentBody.Length > 0)
      {
        currentBody.Append('\n');
      }

      currentBody.Append(line);
    }

    Flush();
    return blocks;
  }

  private static bool IsAssistantOrTool(string? role)
  {
    if (string.IsNullOrWhiteSpace(role))
    {
      return false;
    }

    return role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
      || role.Equals("tool", StringComparison.OrdinalIgnoreCase)
      || role.Equals("tool_result", StringComparison.OrdinalIgnoreCase)
      || role.Equals("toolresult", StringComparison.OrdinalIgnoreCase);
  }

  private static string? TryReadString(JsonElement element, params string[] keys)
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
}

public sealed record TranscriptBlock(string Role, string Text);
