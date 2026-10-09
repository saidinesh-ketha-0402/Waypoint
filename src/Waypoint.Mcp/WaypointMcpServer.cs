using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Waypoint.Mcp;

public static class WaypointMcpServer
{
  public static async Task RunAsync(string repoRoot, CancellationToken cancellationToken)
  {
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
      options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(new WaypointContext(repoRoot));
    builder.Services
      .AddMcpServer(options => options.ServerInfo = new() { Name = "waypoint", Version = "0.1.0" })
      .WithStdioServerTransport()
      .WithTools<WaypointTools>();

    await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
  }
}
