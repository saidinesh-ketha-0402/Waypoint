using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class SymbolResolver(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public IReadOnlyList<SymbolCandidate> Resolve(string input, int maxCandidates = 5)
  {
    if (string.IsNullOrWhiteSpace(input))
    {
      return [];
    }

    var token = input.Trim().Trim('`');
    var results = new List<SymbolCandidate>();
    var seen = new HashSet<long>();

    AddExactQualifiedNameMatches(token, results, seen, maxCandidates);
    if (results.Count >= maxCandidates)
    {
      return results;
    }

    AddDocIdSuffixMatches(token, results, seen, maxCandidates);
    if (results.Count >= maxCandidates)
    {
      return results;
    }

    AddTypeMemberMatches(token, results, seen, maxCandidates);
    if (results.Count >= maxCandidates)
    {
      return results;
    }

    AddTypeNameMatches(token, results, seen, maxCandidates);
    if (results.Count >= maxCandidates)
    {
      return results;
    }

    AddFilePathMatches(token, results, seen, maxCandidates);
    return results;
  }

  private void AddExactQualifiedNameMatches(string token, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, name, qualified_name
      FROM node
      WHERE qualified_name = $token
      ORDER BY id ASC;
      """;
    command.Parameters.AddWithValue("$token", token);
    ReadCandidates(command, results, seen, maxCandidates);
  }

  private void AddDocIdSuffixMatches(string token, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, name, qualified_name
      FROM node
      WHERE qualified_name LIKE $suffix
      ORDER BY
        CASE WHEN qualified_name LIKE $suffix_with_signature THEN 0 ELSE 1 END,
        id ASC;
      """;
    command.Parameters.AddWithValue("$suffix", "%." + token);
    command.Parameters.AddWithValue("$suffix_with_signature", "%." + token + "(%");
    ReadCandidates(command, results, seen, maxCandidates);
  }

  private void AddTypeMemberMatches(string token, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    var normalized = token.TrimEnd('(', ')');
    var dotIndex = normalized.LastIndexOf('.');
    if (dotIndex <= 0 || dotIndex >= normalized.Length - 1)
    {
      return;
    }

    var typeName = normalized[..dotIndex];
    var memberName = normalized[(dotIndex + 1)..];
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT member.id, member.kind, member.name, member.qualified_name
      FROM node member
      JOIN edge e ON e.src = member.id AND e.kind = 'CONTAINS'
      JOIN node type ON type.id = e.dst
      WHERE type.name = $type_name
        AND (member.name = $member_name OR member.qualified_name LIKE $member_suffix)
      ORDER BY member.id ASC;
      """;
    command.Parameters.AddWithValue("$type_name", typeName);
    command.Parameters.AddWithValue("$member_name", memberName);
    command.Parameters.AddWithValue("$member_suffix", "%." + memberName + "(%");
    ReadCandidates(command, results, seen, maxCandidates);
  }

  private void AddTypeNameMatches(string token, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, name, qualified_name
      FROM node
      WHERE name = $name
        AND kind IN ('class', 'interface', 'record', 'struct', 'enum', 'namespace')
      ORDER BY id ASC;
      """;
    command.Parameters.AddWithValue("$name", token);
    ReadCandidates(command, results, seen, maxCandidates);
  }

  private void AddFilePathMatches(string token, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    var normalized = token.Replace('\\', '/');

    using (var exactCommand = _connection.CreateCommand())
    {
      exactCommand.CommandText = """
        SELECT id, kind, name, qualified_name
        FROM node
        WHERE kind = 'file'
          AND (name = $path OR qualified_name = $qualified_name)
        ORDER BY id ASC;
        """;
      exactCommand.Parameters.AddWithValue("$path", normalized);
      exactCommand.Parameters.AddWithValue("$qualified_name", "file:" + normalized);
      ReadCandidates(exactCommand, results, seen, maxCandidates);
    }

    if (results.Count >= maxCandidates)
    {
      return;
    }

    using var suffixCommand = _connection.CreateCommand();
    suffixCommand.CommandText = """
      SELECT id, kind, name, qualified_name
      FROM node
      WHERE kind = 'file'
        AND (name LIKE $suffix OR qualified_name LIKE $qualified_suffix)
      ORDER BY LENGTH(name) ASC, id ASC;
      """;
    suffixCommand.Parameters.AddWithValue("$suffix", "%/" + normalized);
    suffixCommand.Parameters.AddWithValue("$qualified_suffix", "%/" + normalized);
    ReadCandidates(suffixCommand, results, seen, maxCandidates);
  }

  private static void ReadCandidates(SqliteCommand command, IList<SymbolCandidate> results, ISet<long> seen, int maxCandidates)
  {
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      if (results.Count >= maxCandidates)
      {
        return;
      }

      var nodeId = reader.GetInt64(0);
      if (!seen.Add(nodeId))
      {
        continue;
      }

      results.Add(new SymbolCandidate(
        nodeId,
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3)));
    }
  }
}

public sealed record SymbolCandidate(
  long NodeId,
  string Kind,
  string Name,
  string QualifiedName);
