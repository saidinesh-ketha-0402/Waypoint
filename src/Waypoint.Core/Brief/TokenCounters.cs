namespace Waypoint.Core.Brief;

public interface ITokenCounter
{
  int CountTokens(string text);
}

public sealed class CharsDiv4TokenCounter : ITokenCounter
{
  public int CountTokens(string text)
  {
    if (string.IsNullOrEmpty(text))
    {
      return 0;
    }

    return (text.Length + 3) / 4;
  }
}
