using System.Text.RegularExpressions;

namespace Waypoint.Core.Extraction;

public sealed class SentenceSplitter
{
  private static readonly Regex SentenceBoundaryRegex = new(
    @"(?<=[.!?])\s+(?=[A-Z`*\-])",
    RegexOptions.Compiled);

  private static readonly Regex HeadingRegex = new(
    @"^(#+\s*)?(summary|what i did|changes|done)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex BulletRegex = new(
    @"^\s*(?:[-*]|\d+\.)\s+",
    RegexOptions.Compiled);

  public IReadOnlyList<SentenceBlock> Split(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return [];
    }

    var blocks = new List<SentenceBlock>();
    var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    var lines = normalized.Split('\n');

    var inCodeFence = false;
    var inSummaryBlock = false;
    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      var trimmed = line.Trim();
      if (trimmed.StartsWith("```", StringComparison.Ordinal))
      {
        inCodeFence = !inCodeFence;
        continue;
      }

      if (inCodeFence)
      {
        continue;
      }

      if (trimmed.Length == 0)
      {
        inSummaryBlock = false;
        continue;
      }

      if (trimmed.StartsWith("#", StringComparison.Ordinal))
      {
        inSummaryBlock = HeadingRegex.IsMatch(trimmed);
        continue;
      }

      if (HeadingRegex.IsMatch(trimmed))
      {
        inSummaryBlock = true;
        continue;
      }

      var sentenceSource = trimmed;
      var isBullet = BulletRegex.IsMatch(sentenceSource);
      if (isBullet)
      {
        sentenceSource = BulletRegex.Replace(sentenceSource, string.Empty, 1);
      }

      if (sentenceSource.Length == 0)
      {
        continue;
      }

      if (isBullet)
      {
        blocks.Add(new SentenceBlock(sentenceSource, inSummaryBlock));
        continue;
      }

      foreach (var sentence in SentenceBoundaryRegex.Split(sentenceSource))
      {
        var chunk = sentence.Trim();
        if (chunk.Length == 0)
        {
          continue;
        }

        blocks.Add(new SentenceBlock(chunk, inSummaryBlock));
      }
    }

    return blocks;
  }
}

public sealed record SentenceBlock(string Text, bool IsSummaryBlock);
