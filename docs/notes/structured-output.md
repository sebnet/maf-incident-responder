# Structured output — notes

- Phase: 1
- Samples studied: `../agent-framework/dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput`
- MAF version: 1.28.0 (Microsoft.Extensions.AI 10.11.0)

## The problem it solves

The eval runner has to compare the agent's answer with the scenario's ground truth, and in Phase 3 the next workflow
step has to act on it. Free text can't be used for either. "The problem seems to be in the catalog service" has to be
turned into `catalog-api` somehow: a regex, or a second model call. Both are fragile, and the second one costs money.
Structured output makes the model's final answer a JSON document that follows a schema generated from a C# type, so
the answer *is* a `Diagnosis`.

## How it works

The type is the contract. Its `[Description]`s go into the JSON schema the model receives, so they're part of the
prompt:

```csharp
// IncidentResponder.Core/Incidents.cs
public sealed record Diagnosis(
    [property: Description("Service where the root cause is, which may differ from the alerting service")] string RootCauseService,
    [property: Description("What is wrong in the root-cause service. ApplicationError: ... DependencyFailure: ...")] RootCauseCategory Category,
    [property: Description("Root cause in one sentence")] string RootCause,
    [property: Description("0..1")] double Confidence,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<string> SuggestedActions);
```

`RunAsync<T>` builds the schema from `T`, sets it as `ChatOptions.ResponseFormat` for the run, and deserializes the
final answer:

```csharp
// OnCallAssistant.DiagnoseAsync
AgentSession agentSession = await agent.CreateSessionAsync(cancellationToken);
return await agent.RunAsync<Diagnosis>(DiagnoseRequest(alert), agentSession, cancellationToken: cancellationToken);
```

- **Tools still work.** The model keeps calling tools during the run. Only the *final* answer has to follow the
  schema (`Returns_the_typed_diagnosis_the_model_produces_after_using_tools`).
- **The response is the whole run.** `AgentResponse<Diagnosis>` has `Result` (the typed diagnosis), plus `Usage` and
  `Messages` like any `AgentResponse`. That's why `DiagnoseAsync` returns the response and not only the `Diagnosis`:
  the eval runner needs the tokens and tool calls too (`dcde014`).
- **The sample shows the other ways.** `ChatResponseFormat.ForJsonSchema<T>()` on the agent's options, then
  `JsonSerializer.Deserialize<T>(response.Text)`. That's for when the JSON is forwarded as text, when the type is only
  known at run time, and for streaming. `UseStructuredOutput(chatClient)` is middleware that turns a text answer into
  `T` with a second model call, for agents or models that can't do structured output natively.

Ground truth is compared on two fields only, `RootCauseService` and `Category`, plus required terms in the text
(`evals/IncidentResponder.Evals/Scoring.cs`). Those are the fields the type has to get exactly right. The rest is for
humans.

## When NOT to use it

- **Chat with a human.** The CLI's free chat stays text. Forcing a schema on "what about catalog-api?" makes the
  answer worse, not better. `/diagnose` is the only structured path.
- **When the shape isn't known yet.** A schema forces the model to fill every field, including the ones that don't
  fit the case. The categories took a revision: s01's ground truth was first `DependencyFailure` (orders-api's point
  of view), until the rule "the category describes the root-cause service" made it `ApplicationError` (`8764ac6`). Free
  text first, then a schema, is cheaper than fixing the schema afterwards.
- **As a stand-in for judgment.** A schema guarantees the *shape*, not the truth. `confidence` is whatever the model
  writes (`1` in an s01 trace, with no calibration behind it), and an `Evidence.Source` can name a tool that was never
  called (`evals/results/phase-1.md`). Checking that is code's job (a future scorer), not the schema's.
- **For a model or provider without native support.** `RunAsync<T>` assumes the model enforces the schema. Without that
  you get malformed JSON at random. Use `UseStructuredOutput` (an extra call) or a model that supports it.

## Gotchas

- **Enum value descriptions don't reach the model.** The schema lists only the enum *names*. The `[Description]` on each
  `RootCauseCategory` value is dropped (verified with MAF 1.28.0 / M.E.AI 10.11.0). The category definitions are
  therefore repeated in the description of `Diagnosis.Category`, which does reach the model. A test watches for the
  day this changes (`The_schema_explains_the_categories_on_the_property_because_enum_value_descriptions_are_dropped`).
- **Enums need a string converter on the type.** The schema tells the model to write `"ApplicationError"`, so
  deserialization must read names whatever `JsonSerializerOptions` are used. Hence
  `[JsonConverter(typeof(JsonStringEnumConverter<RootCauseCategory>))]` on the enum itself.
- **`Result` deserializes when you read it, not when the run ends.** The run succeeds and `response.Result` throws: an
  `InvalidOperationException` ("did not contain JSON") for an empty answer, a `JsonException` for JSON that doesn't fit
  (`An_answer_without_JSON_fails_with_a_clear_error`, `An_answer_that_does_not_fit_Diagnosis_fails_loudly`). The eval
  runner reads `Usage` and `Messages` *before* `Result`, so a run whose answer doesn't parse still has its numbers.
- **Descriptions are prompt.** Changing a `[Description]` on `Diagnosis` changes what the model reads, just like
  editing `Instructions`, so it's followed by an eval run (AGENTS.md principle 8). They live in `Core`, which has no AI
  dependency: `System.ComponentModel.Description` is plain .NET.
- **The schema is in the request, not on the `invoke_agent` span.** To see what the model was asked to produce, look at
  the `chat` spans (with sensitive telemetry on), like the instructions.
- **Fresh session per diagnosis.** Not a structured-output rule, but it matters for evals: a diagnosis must not see an
  earlier one's history, or runs aren't comparable (`Each_diagnosis_starts_from_a_fresh_session`).

## Numbers

- Phase 1 baseline (`evals/results/phase-1.md`): 6/6 answers parsed into a `Diagnosis`, 0 errors. Service and
  category correct in 6/6.
- Output tokens: ~314–344 per diagnosis, almost all of it the final JSON (tool calls are short). At gpt-4.1-mini
  prices that's about 20–25% of the cost of a run. Input (re-sent on every round trip) is the rest.
- Not measured: the same runs without a schema (free text plus a parser or a second call), to see whether the schema
  costs accuracy. Some studies report that strict formats can hurt reasoning. Here the investigation happens in the
  tool round trips before the JSON is written, which should limit that effect, but that's an assumption.
