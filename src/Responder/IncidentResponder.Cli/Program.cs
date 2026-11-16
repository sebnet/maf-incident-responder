using System.Text.Json;
using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Phase 1 entry point: an interactive on-call assistant in the terminal.
// Config: appsettings.json + user-secrets (Development). See README "Run".
var builder = Host.CreateApplicationBuilder(args);

var endpoint = builder.Configuration["Foundry:ProjectEndpoint"];
if (string.IsNullOrWhiteSpace(endpoint))
{
    Console.Error.WriteLine("Foundry:ProjectEndpoint is not set. Run: dotnet user-secrets set \"Foundry:ProjectEndpoint\" \"https://<resource>.services.ai.azure.com/api/projects/<project>\"");
    return 1;
}

var model = builder.Configuration["Foundry:Model"] ?? "gpt-5.4-mini";
var lgtm = new LgtmOptions(
    new Uri(builder.Configuration["Lgtm:Loki"] ?? LgtmOptions.LocalDefaults.Loki.ToString()),
    new Uri(builder.Configuration["Lgtm:Prometheus"] ?? LgtmOptions.LocalDefaults.Prometheus.ToString()),
    new Uri(builder.Configuration["Lgtm:Tempo"] ?? LgtmOptions.LocalDefaults.Tempo.ToString()));

// The agent's own traces and metrics → LGTM (Tempo / Prometheus), next to the lab's. Logs stay on the console:
// exported, they would land in Loki next to the logs the agent's tools query.
var telemetry = builder.Configuration.GetSection("Telemetry");
var otlp = new Uri(telemetry["OtlpEndpoint"] ?? "http://localhost:4319");
var sensitiveTelemetry = telemetry.GetValue<bool>("SensitiveData");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(telemetry["ServiceName"] ?? "incident-responder"))
    .WithTracing(tracing => tracing
        .AddSource(OnCallAssistant.TelemetrySourceName)
        .AddHttpClientInstrumentation() // tool queries to Loki/Prometheus/Tempo and calls to Foundry, as child spans
        .AddOtlpExporter(o => { o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Endpoint = new Uri(otlp, "v1/traces"); }))
    .WithMetrics(metrics => metrics
        .AddMeter(OnCallAssistant.TelemetrySourceName) // token usage and operation duration
        .AddOtlpExporter(o => { o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Endpoint = new Uri(otlp, "v1/metrics"); }));
// Not UseOtlpExporter(): it also turns on log export. A programmatic Endpoint is used as-is, hence the /v1/... paths.

builder.Services.Configure<BudgetOptions>(builder.Configuration.GetSection("Budget"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(lgtm);
builder.Services.AddHttpClient<ITelemetryQuery, LgtmTelemetryQuery>();
builder.Services.AddSingleton<TelemetryTools>();
builder.Services.AddSingleton<ToolCallMiddleware>();
builder.Services.AddSingleton<IChatClient>(_ => ChatClientFactory.CreateFoundry(new Uri(endpoint), model));
builder.Services.AddSingleton(sp => OnCallAssistant.Create(
    sp.GetRequiredService<IChatClient>(),
    sp.GetRequiredService<TelemetryTools>(),
    sp.GetRequiredService<ToolCallMiddleware>(),
    sensitiveTelemetry));

using var host = builder.Build();
await host.StartAsync(); // starts the OpenTelemetry providers (hosted service); StopAsync at the end flushes them

var agent = host.Services.GetRequiredService<AIAgent>();
AgentSession session = await agent.CreateSessionAsync();

var time = host.Services.GetRequiredService<TimeProvider>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

Console.WriteLine($"{OnCallAssistant.Name} ({model}). Ask e.g. \"what's wrong with orders-api?\". Empty line to exit.");
Console.WriteLine("Commands: /new (new chat session), /diagnose <service> (structured diagnosis, always a fresh session).");
while (true)
{
    Console.Write("\n> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        break;
    }

    if (input.Trim() == "/new")
    {
        session = await agent.CreateSessionAsync();
        Console.WriteLine("(new session)");
        continue;
    }

    if (input.StartsWith("/diagnose", StringComparison.OrdinalIgnoreCase))
    {
        var service = input["/diagnose".Length..].Trim();
        if (service.Length == 0)
        {
            Console.WriteLine("Usage: /diagnose <service>, e.g. /diagnose orders-api");
            continue;
        }

        // A manual alert: the eval runner will pass the scenario's real one instead.
        var alert = new Alert(service, "manual", $"Manual diagnosis requested for {service}.", time.GetUtcNow());
        var response = await OnCallAssistant.DiagnoseAsync(agent, alert);
        Console.WriteLine(JsonSerializer.Serialize(response.Result, json));
        Console.WriteLine($"(tokens: {response.Usage?.InputTokenCount} in, {response.Usage?.OutputTokenCount} out)");
        continue;
    }

    await foreach (var update in agent.RunStreamingAsync(input, session))
    {
        Console.Write(update);
    }

    Console.WriteLine();
}

await host.StopAsync();
return 0;
