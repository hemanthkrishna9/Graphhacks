using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ripple.Core.Graph;

// Ripple MCP server (stdio). Exposes the code graph's domain queries to any MCP client.
//   FALKORDB      host:port of FalkorDB      (default localhost:6379)
//   RIPPLE_GRAPH  graph to query             (default eshop)
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout is the MCP channel

builder.Services.AddSingleton(_ => new GraphStore(
    Environment.GetEnvironmentVariable("FALKORDB") ?? "localhost:6379",
    Environment.GetEnvironmentVariable("RIPPLE_GRAPH") ?? "eshop"));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
