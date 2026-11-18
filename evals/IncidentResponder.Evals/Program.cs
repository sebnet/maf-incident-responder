using IncidentResponder.Agents;
using IncidentResponder.Evals;
using IncidentResponder.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Eval runner. Needs the lab running (it injects faults through /_ops/runtime) and Foundry access (real model, real cost).
// Settings: appsettings.json, user-secrets (shared with the CLI), environment, and the command line, e.g.
//   dotnet run --project evals/IncidentResponder.Evals -- --Evals:Scenarios s01,s02 --Evals:Repetitions 3
var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

var endpoint = config["Foundry:ProjectEndpoint"];
if (string.IsNullOrWhiteSpace(endpoint))
{
    Console.Error.WriteLine("Foundry:ProjectEndpoint is not set (same setting as the CLI: user-secrets or Foundry__ProjectEndpoint).");
    return 1;
}

var model = config["Foundry:Model"] ?? "gpt-5.4-mini";
var evals = config.GetSection("Evals");
var settle = TimeSpan.FromMinutes(evals.GetValue("SettleMinutes", 5.0));
var baseline = TimeSpan.FromMinutes(evals.GetValue("BaselineMinutes", 5.0));
if (baseline > settle)
{
    // The tools may look back to (injection − baseline): that time must be clean, or the run sees the previous one.
    Console.Error.WriteLine($"Evals:BaselineMinutes ({baseline.TotalMinutes}) must not exceed Evals:SettleMinutes ({settle.TotalMinutes}).");
    return 1;
}

var repoRoot = RepoInfo.FindRoot(AppContext.BaseDirectory);
var only = (evals["Scenarios"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var scenarios = ScenarioLoader.LoadReady(Path.Combine(repoRoot, "evals", "scenarios"), only);
var prices = config.GetSection("Prices").Get<Dictionary<string, ModelPrice>>() ?? [];
var settings = new EvalSettings(
    evals["Phase"] ?? "phase-1",
    evals.GetValue("Repetitions", 3),
    settle,
    baseline,
    model,
    RepoInfo.Commit(repoRoot),
    RepoInfo.MafVersion(),
    prices);

var labServices = config.GetSection("Lab:Services").GetChildren().ToDictionary(c => c.Key, c => new Uri(c.Value!));
var lgtm = new LgtmOptions(
    new Uri(config["Lgtm:Loki"] ?? LgtmOptions.LocalDefaults.Loki.ToString()),
    new Uri(config["Lgtm:Prometheus"] ?? LgtmOptions.LocalDefaults.Prometheus.ToString()),
    new Uri(config["Lgtm:Tempo"] ?? LgtmOptions.LocalDefaults.Tempo.ToString()));

// The agent's spans (and one neutral parent span per run) → LGTM, so a run can be inspected in Tempo by its trace id.
// No HttpClient instrumentation: it would also trace the runner's calls to /_ops/runtime (the fault injection) into
// the Tempo the agent reads. Disposing the provider at the end flushes what is pending.
var telemetry = config.GetSection("Telemetry");
var otlp = new Uri(telemetry["OtlpEndpoint"] ?? "http://localhost:4319");
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(resource => resource.AddService(telemetry["ServiceName"] ?? "incident-responder-evals"))
    .AddSource(OnCallAssistant.TelemetrySourceName, EvalRunner.ActivitySourceName)
    .AddOtlpExporter(o => { o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Endpoint = new Uri(otlp, "v1/traces"); })
    .Build();

using var host = builder.Build();
using var http = new HttpClient();
var context = new EvalContext(
    ChatClientFactory.CreateFoundry(new Uri(endpoint), model),
    new LgtmTelemetryQuery(http, lgtm),
    TimeProvider.System,
    Options.Create(config.GetSection("Budget").Get<BudgetOptions>() ?? new BudgetOptions()),
    host.Services.GetRequiredService<ILoggerFactory>(),
    telemetry.GetValue<bool>("SensitiveData"));
var runner = new EvalRunner(context, new LabControl(http, labServices), settings, Console.Out);

var runsDir = Path.Combine(repoRoot, "evals", "runs");
Directory.CreateDirectory(runsDir);
var runsFile = Path.Combine(runsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{settings.Phase}.jsonl");

var perRun = scenarios.Average(s => (settle + TimeSpan.FromSeconds(s.WarmupSeconds) + TimeSpan.FromMinutes(0.5)).TotalMinutes);
Console.WriteLine($"""
    Eval {settings.Phase}: {string.Join(", ", scenarios.Select(s => s.Id))} × {settings.Repetitions}
      model {model} (estimated price {(prices.ContainsKey(model) ? "configured" : "NOT configured: cost will be unknown")})
      commit {settings.Commit}, MAF {settings.MafVersion}
      ~{perRun * scenarios.Count * settings.Repetitions:0} min (settle {settle.TotalMinutes} min + warmup + diagnosis, per run)
      runs → {runsFile}
    Ctrl+C stops after clearing the active fault.
    """);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await runner.RunAsync(scenarios, runsFile, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled. Faults cleared; finished runs are in the runs file.");
}

Console.WriteLine();
Console.WriteLine($"Model {model}, commit {settings.Commit}, MAF {settings.MafVersion}, {DateTime.UtcNow:yyyy-MM-dd}");
Console.WriteLine(Summary.ToMarkdown(runner.Records));
return 0;
