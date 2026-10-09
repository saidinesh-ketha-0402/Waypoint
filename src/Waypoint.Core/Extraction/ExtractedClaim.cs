namespace Waypoint.Core.Extraction;

public static class ClaimKinds
{
  public const string Decision = "decision";
  public const string Constraint = "constraint";
  public const string AttemptFailed = "attempt_failed";
  public const string OpenItem = "open_item";
  public const string Fact = "fact";
}

public sealed record ExtractedClaim(
  string Kind,
  string Text,
  double Confidence,
  long? AboutNodeId,
  string Source);
