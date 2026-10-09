using System.Text.RegularExpressions;
using Waypoint.Core.Queries;

namespace Waypoint.Core.Extraction;

public sealed class SymbolLinker(SymbolResolver resolver)
{
  private static readonly Regex BacktickedRegex = new(
    @"`(?<token>[^`]+)`",
    RegexOptions.Compiled);

  private static readonly Regex TypeMemberRegex = new(
    @"\b(?<token>[A-Z]\w*(?:\.[A-Za-z_]\w+)+)(?=\(|\b)",
    RegexOptions.Compiled);

  private static readonly Regex InterfaceRegex = new(
    @"\b(?<token>I[A-Z]\w+)\b",
    RegexOptions.Compiled);

  private static readonly Regex FileRegex = new(
    @"(?<token>\S+\.(?:cs|csproj|json|cshtml|razor))",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex FileWithLineRegex = new(
    @"(?<token>\S+\.(?:cs|csproj|json|cshtml|razor))\(\d+,\d+\)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private readonly SymbolResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

  public long? Link(string sentence)
  {
    if (string.IsNullOrWhiteSpace(sentence))
    {
      return null;
    }

    foreach (var token in EnumerateTokens(sentence))
    {
      var candidates = _resolver.Resolve(token, maxCandidates: 5);
      if (candidates.Count == 1)
      {
        return candidates[0].NodeId;
      }
    }

    return null;
  }

  private static IEnumerable<string> EnumerateTokens(string text)
  {
    foreach (var token in MatchTokens(BacktickedRegex, text))
    {
      yield return token;
    }

    foreach (var token in MatchTokens(TypeMemberRegex, text))
    {
      yield return token;
    }

    foreach (var token in MatchTokens(InterfaceRegex, text))
    {
      yield return token;
    }

    foreach (var token in MatchTokens(FileRegex, text))
    {
      yield return token;
    }

    foreach (var token in MatchTokens(FileWithLineRegex, text))
    {
      yield return token;
    }
  }

  private static IEnumerable<string> MatchTokens(Regex regex, string text)
  {
    foreach (Match match in regex.Matches(text))
    {
      if (!match.Success)
      {
        continue;
      }

      var value = match.Groups["token"].Value.Trim();
      if (value.Length > 0)
      {
        yield return value;
      }
    }
  }
}
