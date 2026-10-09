using System.Text.RegularExpressions;
using Waypoint.Core.Parsing;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone2DotnetParserTests
{
  private static readonly string FixtureRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "dotnet-output"));

  [Fact]
  public void Build_cs_errors_are_parsed_and_deduped()
  {
    var output = ReadFixture("build-cs-errors.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet build DemoShop.slnx", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("build", result!.DotnetCommand);
    Assert.Equal("failed", result.Build);
    Assert.True(result.Failed);
    Assert.Equal(2, result.Errors.Count(error => error.Severity == "error"));
    Assert.Contains(result.Errors, error => error.Code == "CS0103" && error.File.EndsWith("src/Shop.Core/Services/BuildBreak.cs", StringComparison.Ordinal) && error.Line == 8);
    Assert.Contains(result.Errors, error => error.Code == "CS1061" && error.File.EndsWith("src/Shop.Core/Services/BuildBreak.cs", StringComparison.Ordinal) && error.Line == 10);
  }

  [Fact]
  public void Test_vstest_failure_summary_is_parsed()
  {
    var output = ReadFixture("test-vstest-fail.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("test", result!.DotnetCommand);
    Assert.NotNull(result.Tests);
    Assert.Equal(2, result.Tests!.Passed);
    Assert.Equal(1, result.Tests.Failed);
    Assert.Equal(0, result.Tests.Skipped);
    Assert.Contains("Shop.UnitTests.PricingTests.Clamps_to_50_percent", result.Tests.FailedNames);
    Assert.True(result.Failed);
  }

  [Fact]
  public void Test_mtp_failure_summary_is_parsed()
  {
    var output = ReadFixture("test-mtp-fail.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj --use-mtp", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("test", result!.DotnetCommand);
    Assert.NotNull(result.Tests);
    Assert.Equal(2, result.Tests!.Passed);
    Assert.Equal(1, result.Tests.Failed);
    Assert.Equal(0, result.Tests.Skipped);
    Assert.True(result.Failed);
  }

  [Fact]
  public void Run_scoped_singleton_reports_exception_and_di_error()
  {
    var output = ReadFixture("run-di-scoped-singleton.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet run --project src/Shop.Web", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("run", result!.DotnetCommand);
    Assert.NotNull(result.Exception);
    Assert.Equal("System.InvalidOperationException", result.Exception!.Type);
    Assert.Contains("ScopedInSingletonException", result.DiError);
    Assert.True(result.Failed);
  }

  [Fact]
  public void Run_unresolved_service_reports_di_error()
  {
    var output = ReadFixture("run-unresolved-service.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet run --project src/Shop.Web", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("run", result!.DotnetCommand);
    Assert.NotNull(result.Exception);
    Assert.Contains("NoServiceRegistered", result.DiError);
    Assert.True(result.Failed);
  }

  [Fact]
  public void Ef_migrations_add_detects_added_migration()
  {
    var output = ReadFixture("ef-migrations-add.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet ef migrations add AddDiscountPolicy", output, exitCode: 0);

    Assert.NotNull(result);
    Assert.Equal("ef", result!.DotnetCommand);
    Assert.NotNull(result.Ef);
    Assert.Equal("AddDiscountPolicy", result.Ef!.MigrationAdded);
    Assert.Contains("Microsoft.EntityFrameworkCore.Design", result.Ef.Error);
  }

  [Fact]
  public void Ef_pending_model_changes_is_captured_as_error()
  {
    var output = ReadFixture("ef-pending-model-changes.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet ef migrations add AddDiscountPolicy", output, exitCode: 1);

    Assert.NotNull(result);
    Assert.Equal("ef", result!.DotnetCommand);
    Assert.NotNull(result.Ef);
    Assert.Contains("Microsoft.EntityFrameworkCore.Design", result.Ef!.Error);
    Assert.True(result.Failed);
  }

  [Fact]
  public void Build_success_marks_not_failed()
  {
    var output = ReadFixture("build-success.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet build DemoShop.slnx", output, exitCode: 0);

    Assert.NotNull(result);
    Assert.Equal("build", result!.DotnetCommand);
    Assert.Equal("ok", result.Build);
    Assert.False(result.Failed);
    Assert.Empty(result.Errors);
  }

  [Fact]
  public void Test_vstest_pass_marks_not_failed()
  {
    var output = ReadFixture("test-vstest-pass.txt");
    var parser = CreateParserForOutput(output);

    var result = parser.Parse("dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj", output, exitCode: 0);

    Assert.NotNull(result);
    Assert.Equal("test", result!.DotnetCommand);
    Assert.NotNull(result.Tests);
    Assert.Equal(3, result.Tests!.Passed);
    Assert.Equal(0, result.Tests.Failed);
    Assert.False(result.Failed);
  }

  [Fact]
  public void Non_dotnet_command_returns_null()
  {
    var parser = new DotnetOutputParser(string.Empty);
    var result = parser.Parse("npm test", "irrelevant output", exitCode: 1);
    Assert.Null(result);
  }

  private static DotnetOutputParser CreateParserForOutput(string output) =>
    new(GuessRepoRoot(output));

  private static string GuessRepoRoot(string output)
  {
    var match = Regex.Match(output, @"(?<root>/[^\r\n]+?)/src/");
    return match.Success ? match.Groups["root"].Value : string.Empty;
  }

  private static string ReadFixture(string fileName) =>
    File.ReadAllText(Path.Combine(FixtureRoot, fileName));
}
