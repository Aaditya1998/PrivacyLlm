# PrivacyLlm

A .NET 8 reference implementation of a privacy-first LLM tier for workloads that touch customer names, phone
numbers and addresses under India's DPDP Act 2023.

- **Self-hosted tier:** Qwen3 30B-A3B on vLLM, on Vast.ai GPUs. Handles all personal data.
- **Frontier tier:** Anthropic API. Handles only payloads with no personal data, or task types you have approved for pseudonymised processing.
- **Pipeline:** Hangfire jobs. Payloads are encrypted outside Hangfire storage, every task is audited, and if the self-hosted tier is down the job retries instead of falling back.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design, the DPDP mapping and the model evaluation,
and [deploy/vastai/README.md](deploy/vastai/README.md) for standing up vLLM.

## Layout

```
src/PrivacyLlm.Core            classification, routing policy, consent, pseudonymisation, audit, gateway
src/PrivacyLlm.Infrastructure  vLLM + Anthropic HTTP clients, DI wiring
src/PrivacyLlm.Api             ASP.NET Core minimal API + Hangfire server/dashboard
tests/PrivacyLlm.Tests         xUnit: detector, routing invariants, gateway behaviour
deploy/vastai                  vLLM start script and Vast.ai runbook
```

## Run

```bash
dotnet test
dotnet run --project src/PrivacyLlm.Api
```

Hangfire uses in-memory storage unless `ConnectionStrings:Hangfire` is set (SQL Server). Put secrets in
user-secrets:

```bash
dotnet user-secrets --project src/PrivacyLlm.Api set "SelfHostedLlm:ApiKey" "<vllm key>"
dotnet user-secrets --project src/PrivacyLlm.Api set "FrontierLlm:ApiKey" "<anthropic key>"
```

### Local development with Ollama

No GPU rental needed to try the full pipeline. Ollama serves the same OpenAI-compatible API as vLLM:

```bash
ollama pull qwen3:4b-instruct-2507-q4_K_M
```

```bash
dotnet run --project src/PrivacyLlm.Api --launch-profile ollama
```

The `ollama` launch profile points the self-hosted tier at `http://localhost:11434`, uses `v1/models` as the
health check (Ollama has no `/health`) and allows 300 s per request, because the first call waits for the model
to load. A 4B model is fine for exercising routing and consent. Use the 30B-class model on vLLM for real quality.

## API

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/classify` | Dry run: sensitivity, PII spans (no values) and routing decision |
| POST | `/api/tasks` | Enqueue an `LlmTask` as a Hangfire job → `{ payloadId, jobId }` |
| GET | `/api/tasks/{payloadId}` | Result (kept for 24h) or `pending` |
| POST | `/api/tasks/sync` | Run immediately (for debugging) |
| POST | `/api/consents` | Record consent / legitimate use for a principal + purpose |
| DELETE | `/api/consents/{principal}/{purpose}` | Withdraw |
| GET | `/api/audit?principalId=` | Processing summary |
| GET | `/hangfire` | Dashboard (local requests only) |

Example:

```bash
curl -X POST localhost:5000/api/consents -H "Content-Type: application/json" \
  -d '{"dataPrincipalId":"cust-42","purpose":"order-support","basis":"VoluntarilyProvided"}'

curl -X POST localhost:5000/api/tasks -H "Content-Type: application/json" -d '{
  "taskId":"ord-1001","taskType":"address-normalization","purpose":"order-support","dataPrincipalId":"cust-42",
  "systemPrompt":"Normalise this Indian address to JSON with line1, locality, city, state, pincode.",
  "input":"flat 302 sai residency 14th cross indiranagar blr 560038 call 9876543210",
  "declaredFields":["address","phone"],"jsonMode":true }'
```
