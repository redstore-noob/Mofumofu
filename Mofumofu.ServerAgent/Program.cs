using System.Net;
using MofuCore.Server;
using Mofumofu.ServerAgent;
using Mofumofu.ServerAgent.Core;
using Mofumofu.ServerAgent.Endpoints;
using Mofumofu.ServerAgent.Events;
using Mofumofu.ServerAgent.Files;
using Mofumofu.ServerAgent.Operations;
using Mofumofu.ServerAgent.Probe;
using Mofumofu.ServerAgent.Security;
using Mofumofu.ServerAgent.Services;

string? explicitConfig = DeploymentConfiguration.FindOption(args, "--config");
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (explicitConfig is not null)
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(explicitConfig), optional: false, reloadOnChange: false);
}

// Re-add command-line configuration after the explicit file so ServerAgent:* overrides retain normal precedence.
builder.Configuration.AddCommandLine(args);
AgentOptions options = DeploymentConfiguration.Bind(builder.Configuration, args);
options.NormalizeAndValidate();

// This assignment must remain before constructing CoreGateway or making any other Core call.
ServerManage.ServerRootDirectory = options.ServerRootDirectory;
CoreGateway core = new();
if (core.GetServerInfo(options.InstanceId) is null)
{
    throw new InvalidOperationException(
        $"The existing instance '{options.InstanceId}' was not found under '{options.ServerRootDirectory}'. Remote creation is disabled.");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = AgentOptions.MaxRequestBodyBytes;
    kestrel.Listen(IPAddress.Loopback, options.GetBindPort());
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICoreGateway>(core);
builder.Services.AddSingleton<TokenRegistry>();
builder.Services.AddSingleton<TokenRateLimiter>();
builder.Services.AddSingleton<IAuditSink>(_ => new JsonlAuditSink(options.AuditPath));
builder.Services.AddSingleton<AuditExecutor>();
builder.Services.AddSingleton(_ => new EventJournal(options.EventJournalPath,
    options.EventJournalCapacity, _.GetRequiredService<IClock>()));
builder.Services.AddSingleton<IProbeGateway, HttpProbeGateway>();
builder.Services.AddSingleton<SafeFileService>();
builder.Services.AddSingleton<PropertyPatchService>();
builder.Services.AddSingleton<ConfirmationService>();
builder.Services.AddSingleton<OperationService>();
builder.Services.AddSingleton<PlayerService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<ProbeEventService>();
builder.Services.AddSingleton<ServerMonitorService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerMonitorService>());
builder.Services.AddSingleton<AgentEndpointHandlers>();

WebApplication app = builder.Build();
app.UseRouting();
app.UseMiddleware<RequestSecurityMiddleware>();
AgentEndpoints.Map(app);
app.Lifetime.ApplicationStopping.Register(() =>
{
    // Stop the only Core processes owned by this Agent; never create or reconfigure an instance here.
    core.ShutdownAllAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    app.Services.GetRequiredService<OperationService>().StopAsync().GetAwaiter().GetResult();
});
app.Run();

public static class DeploymentConfiguration
{
    public static string? FindOption(string[] args, string name)
    {
        string? result = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                result = args[index][(name.Length + 1)..];
            }
            else if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{name} requires a value.");
                }

                result = args[++index];
            }
        }

        if (result is not null && string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException($"{name} requires a non-empty value.");
        }

        return result;
    }

    public static AgentOptions Bind(IConfiguration configuration, string[] args)
    {
        AgentOptions options = new();
        configuration.GetSection("ServerAgent").Bind(options);
        if (FindOption(args, "--urls") is { } urls)
        {
            // Multiple listeners are intentionally not supported: Agent has exactly one IPv4 loopback socket.
            options.BindUrl = urls;
        }

        return options;
    }
}

public partial class Program;
