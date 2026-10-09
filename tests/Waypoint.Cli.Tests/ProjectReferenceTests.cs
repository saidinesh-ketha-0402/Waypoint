using Waypoint.Core;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class ProjectReferenceTests
{
  [Fact]
  public void Core_does_not_reference_roslyn_build_or_mcp_assemblies()
  {
    var referencedAssemblies = typeof(CoreMarker).Assembly.GetReferencedAssemblies();

    var forbiddenPrefixes = new[]
    {
      "Microsoft.CodeAnalysis",
      "Microsoft.Build",
      "ModelContextProtocol"
    };

    var forbidden = referencedAssemblies
      .Where(assembly => forbiddenPrefixes.Any(prefix => assembly.Name?.StartsWith(prefix, StringComparison.Ordinal) == true))
      .Select(assembly => assembly.Name)
      .ToArray();

    Assert.Empty(forbidden);
  }
}
