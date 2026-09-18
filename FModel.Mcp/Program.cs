using FModel.Mcp.Runtime;
using FModel.Mcp.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FModel.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // stdio 传输下 stdout 属于协议，任何日志都必须走 stderr
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        NativeLibraries.EnsureLoaded();

        builder.Services.AddSingleton<SessionManager>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }
}
