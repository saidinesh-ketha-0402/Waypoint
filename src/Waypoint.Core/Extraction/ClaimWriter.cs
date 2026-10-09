using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Extraction;

public sealed class ClaimWriter(SqliteConnection connection, TimeProvider? timeProvider = null)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public int WriteClaims(
    long taskId,
    string sessionId,
    long? episodeId,
    IReadOnlyList<ExtractedClaim> claims)
  {
    if (claims.Count == 0)
    {
      return 0;
    }

    var inserted = 0;
    foreach (var claim in claims)
    {
      if (TryMergeDuplicate(taskId, claim))
      {
        continue;
      }

      var newId = InsertClaim(taskId, sessionId, episodeId, claim);
      inserted++;

      if ((claim.Kind is ClaimKinds.Decision or ClaimKinds.Constraint)
          && ClaimClassifier.IsSupersedeCandidate(claim.Text))
      {
        SupersedePreviousClaim(taskId, newId, claim);
      }
    }

    return inserted;
  }

  private bool TryMergeDuplicate(long taskId, ExtractedClaim candidate)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, text, confidence
      FROM claim
      WHERE task_id = $task_id
        AND kind = $kind
        AND status = 'current'
      ORDER BY id DESC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$kind", candidate.Kind);

    long? winnerId = null;
    var winnerConfidence = 0.0;
    var winnerSimilarity = 0.0;
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      var claimId = reader.GetInt64(0);
      var text = reader.GetString(1);
      var confidence = reader.GetDouble(2);
      var similarity = Similarity(Normalize(text), Normalize(candidate.Text));
      if (similarity <= 0.85 || similarity <= winnerSimilarity)
      {
        continue;
      }

      winnerSimilarity = similarity;
      winnerId = claimId;
      winnerConfidence = confidence;
    }

    if (!winnerId.HasValue)
    {
      return false;
    }

    if (candidate.Confidence > winnerConfidence)
    {
      using var update = _connection.CreateCommand();
      update.CommandText = """
        UPDATE claim
        SET confidence = $confidence
        WHERE id = $id;
        """;
      update.Parameters.AddWithValue("$id", winnerId.Value);
      update.Parameters.AddWithValue("$confidence", candidate.Confidence);
      update.ExecuteNonQuery();
    }

    return true;
  }

  private long InsertClaim(long taskId, string sessionId, long? episodeId, ExtractedClaim claim)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT INTO claim(
        task_id, session_id, kind, text, about_node_id, status, confidence, source, derived_from_episode_id, created_at)
      VALUES(
        $task_id, $session_id, $kind, $text, $about_node_id, 'current', $confidence, $source, $episode_id, $created_at);
      SELECT last_insert_rowid();
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$session_id", sessionId);
    command.Parameters.AddWithValue("$kind", claim.Kind);
    command.Parameters.AddWithValue("$text", claim.Text);
    command.Parameters.AddWithValue("$about_node_id", (object?)claim.AboutNodeId ?? DBNull.Value);
    command.Parameters.AddWithValue("$confidence", claim.Confidence);
    command.Parameters.AddWithValue("$source", claim.Source);
    command.Parameters.AddWithValue("$episode_id", (object?)episodeId ?? DBNull.Value);
    command.Parameters.AddWithValue("$created_at", ToUtcIso(_timeProvider.GetUtcNow()));

    var result = command.ExecuteScalar();
    if (result is not long id)
    {
      throw new InvalidOperationException("Unable to insert claim.");
    }

    UpsertClaimFts(id, claim.Text);
    return id;
  }

  private void SupersedePreviousClaim(long taskId, long newClaimId, ExtractedClaim claim)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, text, about_node_id
      FROM claim
      WHERE task_id = $task_id
        AND kind = $kind
        AND status = 'current'
        AND id <> $new_id
      ORDER BY id DESC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$kind", claim.Kind);
    command.Parameters.AddWithValue("$new_id", newClaimId);

    long? targetId = null;
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      var existingId = reader.GetInt64(0);
      var existingText = reader.GetString(1);
      var aboutNode = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
      var sameNode = claim.AboutNodeId.HasValue && aboutNode.HasValue && claim.AboutNodeId == aboutNode;
      var similarity = Similarity(Normalize(claim.Text), Normalize(existingText));
      if (!sameNode && similarity <= 0.5)
      {
        continue;
      }

      targetId = existingId;
      break;
    }

    if (!targetId.HasValue)
    {
      return;
    }

    using var update = _connection.CreateCommand();
    update.CommandText = """
      UPDATE claim
      SET status = 'superseded',
          superseded_by = $superseded_by,
          invalidated_at = $invalidated_at
      WHERE id = $id;
      """;
    update.Parameters.AddWithValue("$id", targetId.Value);
    update.Parameters.AddWithValue("$superseded_by", newClaimId);
    update.Parameters.AddWithValue("$invalidated_at", ToUtcIso(_timeProvider.GetUtcNow()));
    update.ExecuteNonQuery();
  }

  private static string Normalize(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return string.Empty;
    }

    var builder = new StringBuilder(text.Length);
    foreach (var ch in text.ToLowerInvariant())
    {
      if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
      {
        builder.Append(ch);
      }
    }

    return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
  }

  private static double Similarity(string a, string b)
  {
    if (a.Length == 0 && b.Length == 0)
    {
      return 1.0;
    }

    var distance = LevenshteinDistance(a, b);
    var maxLength = Math.Max(a.Length, b.Length);
    return maxLength == 0 ? 1.0 : 1.0 - (distance / (double)maxLength);
  }

  private static int LevenshteinDistance(string a, string b)
  {
    var rows = a.Length + 1;
    var cols = b.Length + 1;
    var matrix = new int[rows, cols];

    for (var i = 0; i < rows; i++)
    {
      matrix[i, 0] = i;
    }

    for (var j = 0; j < cols; j++)
    {
      matrix[0, j] = j;
    }

    for (var i = 1; i < rows; i++)
    {
      for (var j = 1; j < cols; j++)
      {
        var cost = a[i - 1] == b[j - 1] ? 0 : 1;
        matrix[i, j] = Math.Min(
          Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
          matrix[i - 1, j - 1] + cost);
      }
    }

    return matrix[rows - 1, cols - 1];
  }

  private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

  private void UpsertClaimFts(long claimId, string text)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT OR REPLACE INTO claim_fts(rowid, text)
      VALUES($rowid, $text);
      """;
    command.Parameters.AddWithValue("$rowid", claimId);
    command.Parameters.AddWithValue("$text", text);
    command.ExecuteNonQuery();
  }
}
