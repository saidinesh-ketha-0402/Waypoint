using System.Text.RegularExpressions;

namespace Waypoint.Core.Extraction;

public sealed class ClaimClassifier(SymbolLinker symbolLinker, SentenceSplitter? sentenceSplitter = null)
{
  private static readonly Regex AttemptFailedRegex = new(
    @"\b(didn'?t work|failed|reverted|broke|doesn'?t compile)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex OpenItemRegex = new(
    @"\b(todo|remaining|still need|next step|not yet|left to|blocked|follow[- ]up|open item)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex ConstraintRegex = new(
    @"\b(must|must not|should not|can'?t|cannot|requires|only works|is required)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex ConstraintContextRegex = new(
    @"\b(tests?|build|lint|ci|api|schema|scoped|singleton|transient|nullable|async all the way|configureawait|breaking change|public api|migration|connection string|appsettings|treatwarningsaserrors)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex DecisionRegex = new(
    @"\b(decided|chose|going with|opted|will use|instead of|rather than|because|switched to|addscoped|addsingleton|addtransient)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex SupersedeHintRegex = new(
    @"\b(instead|reverted|no longer|switched)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private readonly SymbolLinker _symbolLinker = symbolLinker ?? throw new ArgumentNullException(nameof(symbolLinker));
  private readonly SentenceSplitter _sentenceSplitter = sentenceSplitter ?? new SentenceSplitter();

  public IReadOnlyList<ExtractedClaim> Classify(
    string text,
    string source = "regex",
    bool fromThought = false)
  {
    var sentences = _sentenceSplitter.Split(text);
    var claims = new List<ExtractedClaim>(sentences.Count);

    foreach (var sentence in sentences)
    {
      var kind = ClassifyKind(sentence.Text);
      if (kind is null)
      {
        continue;
      }

      var aboutNodeId = _symbolLinker.Link(sentence.Text);
      var confidence = ComputeConfidence(
        sentence.Text,
        source,
        fromThought,
        sentence.IsSummaryBlock,
        aboutNodeId.HasValue);

      claims.Add(new ExtractedClaim(kind, sentence.Text, confidence, aboutNodeId, source));
    }

    return claims;
  }

  public static bool IsSupersedeCandidate(string text) =>
    !string.IsNullOrWhiteSpace(text) && SupersedeHintRegex.IsMatch(text);

  private static string? ClassifyKind(string sentence)
  {
    var trimmed = sentence.TrimStart();
    if (trimmed.StartsWith("failed attempt:", StringComparison.OrdinalIgnoreCase))
    {
      return ClaimKinds.AttemptFailed;
    }

    if (trimmed.StartsWith("open item:", StringComparison.OrdinalIgnoreCase))
    {
      return ClaimKinds.OpenItem;
    }

    if (trimmed.StartsWith("constraint:", StringComparison.OrdinalIgnoreCase))
    {
      return ClaimKinds.Constraint;
    }

    if (trimmed.StartsWith("decision:", StringComparison.OrdinalIgnoreCase))
    {
      return ClaimKinds.Decision;
    }

    if (AttemptFailedRegex.IsMatch(sentence))
    {
      return ClaimKinds.AttemptFailed;
    }

    if (OpenItemRegex.IsMatch(sentence))
    {
      return ClaimKinds.OpenItem;
    }

    if (ConstraintRegex.IsMatch(sentence) && ConstraintContextRegex.IsMatch(sentence))
    {
      return ClaimKinds.Constraint;
    }

    if (DecisionRegex.IsMatch(sentence))
    {
      return ClaimKinds.Decision;
    }

    return null;
  }

  private static double ComputeConfidence(
    string sentence,
    string source,
    bool fromThought,
    bool isSummaryBlock,
    bool hasNodeLink)
  {
    var confidence = 0.5;
    if (hasNodeLink)
    {
      confidence += 0.2;
    }

    if (isSummaryBlock)
    {
      confidence += 0.2;
    }

    if (string.Equals(source, "tool_output", StringComparison.Ordinal))
    {
      confidence += 0.3;
    }

    if (sentence.TrimEnd().EndsWith("?", StringComparison.Ordinal))
    {
      confidence -= 0.2;
    }

    if (fromThought)
    {
      confidence *= 0.7;
    }

    return Math.Clamp(confidence, 0.0, 1.0);
  }
}
