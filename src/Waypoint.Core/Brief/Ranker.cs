using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Brief;

public sealed class Ranker(TimeProvider? timeProvider = null)
{
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public IReadOnlyList<RankedClaim> RankClaims(
    IReadOnlyList<ClaimRecord> claims,
    ISet<long> touchedInPreviousSession)
  {
    ArgumentNullException.ThrowIfNull(claims);
    ArgumentNullException.ThrowIfNull(touchedInPreviousSession);

    var ranked = new List<RankedClaim>(claims.Count);
    foreach (var claim in claims)
    {
      var touched = claim.AboutNodeId.HasValue && touchedInPreviousSession.Contains(claim.AboutNodeId.Value);
      var score = ScoreClaim(claim, touched);
      ranked.Add(new RankedClaim(claim, score));
    }

    ranked.Sort(static (left, right) =>
    {
      var byScore = right.Score.CompareTo(left.Score);
      if (byScore != 0)
      {
        return byScore;
      }

      return right.Claim.Id.CompareTo(left.Claim.Id);
    });
    return ranked;
  }

  private double ScoreClaim(ClaimRecord claim, bool touchedInPreviousSession)
  {
    var now = _timeProvider.GetUtcNow().UtcDateTime;
    var createdAt = ParseIso(claim.CreatedAtUtcIso);
    var ageHours = Math.Max(0, (now - createdAt).TotalHours);

    var score = claim.Confidence * Math.Exp(-ageHours / 72.0);
    if (string.Equals(claim.Kind, "open_item", StringComparison.Ordinal))
    {
      score *= 2.0;
    }

    if (touchedInPreviousSession)
    {
      score *= 1.5;
    }

    if (string.Equals(claim.Source, "tool_output", StringComparison.Ordinal) ||
        string.Equals(claim.Source, "explicit", StringComparison.Ordinal))
    {
      score *= 1.2;
    }

    return score;
  }

  private static DateTime ParseIso(string value) =>
    DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

public sealed record ClaimRecord(
  long Id,
  string Kind,
  string Text,
  double Confidence,
  string Source,
  long? AboutNodeId,
  string CreatedAtUtcIso,
  string? SessionId);

public sealed record RankedClaim(ClaimRecord Claim, double Score);
