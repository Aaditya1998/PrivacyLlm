# PrivacyLlm

A .NET 8 reference implementation of a privacy-first LLM tier for workloads that touch customer names, phone
numbers and addresses under India's DPDP Act 2023.

- **Self-hosted tier:** Qwen3 30B-A3B on vLLM, on Vast.ai GPUs. Handles all personal data.
- **Frontier tier:** Anthropic API. Handles only payloads with no personal data, or task types you have approved for pseudonymised processing.
- **Name detection:** finds unlabelled Indian names ("Ravi said…", "Hi Priya,", "Anil bhai ka order") with
  curated word lists and context cues, no model; 93.8% recall on an independent held-out set.
- **Pipeline:** Hangfire jobs. Payloads are encrypted outside Hangfire storage, every task is audited, and if the self-hosted tier is down the job retries instead of falling back.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design, the DPDP mapping and the model evaluation,
and [deploy/vastai/README.md](deploy/vastai/README.md) for standing up vLLM.

## Layout

```
src/PrivacyLlm.Core            classification, routing policy, consent, pseudonymisation, audit, gateway
src/PrivacyLlm.Infrastructure  vLLM + Anthropic HTTP clients, DI wiring
src/PrivacyLlm.Persistence     EF Core stores (SQLite / SQL Server) for consent, audit, payloads, key ring
src/PrivacyLlm.Api             ASP.NET Core minimal API + Hangfire server/dashboard
tests/PrivacyLlm.Tests         xUnit: detector, routing invariants, gateway behaviour
deploy/vastai                  vLLM start script and Vast.ai runbook
```

## Run

```bash
dotnet test
dotnet run --project src/PrivacyLlm.Api
```

Storage is in-memory by default. For data that survives restarts, set `Storage:Provider` to `Sqlite` or
`SqlServer` and `ConnectionStrings:PrivacyLlm`. Consent, the audit trail, encrypted payloads and the Data
Protection keys then go to that database (the `ollama` profile uses `privacyllm.db`). Hangfire's own queue uses
SQL Server when `ConnectionStrings:Hangfire` is set, otherwise memory. Put secrets in user-secrets:

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

## Authentication

Every `/api` endpoint needs an API key in the `X-Api-Key` header. Configuration stores only the SHA-256 of each
key, never the key itself. Create one:

```bash
dotnet run --project src/PrivacyLlm.Api -- generate-api-key
```

Give the printed key to the client and put the hash in configuration (user-secrets locally, a secret store in
production) with the scopes that client needs:

```json
"Auth": { "Clients": [ { "Name": "order-service", "KeySha256": "<hash>", "Scopes": [ "tasks" ] } ] }
```

| Scope | Allows |
|---|---|
| `tasks` | `POST /api/classify`, `POST /api/tasks`, `POST /api/tasks/sync`, `GET /api/tasks/{id}` (own tasks only) |
| `consent:write` | `POST /api/consents`, `DELETE /api/consents/...` |
| `consent:read` | `GET /api/consents/{principal}` |
| `audit:read` | `GET /api/audit` |

- A client can only read results of tasks it queued itself; anyone else gets 404, the same as an unknown id.
- Task creation is limited per client (`RateLimiting:TasksPerMinute`, default 60); over the limit returns 429.
- With no clients configured, every `/api` request is rejected and the app logs a warning at startup.
- Invalid configuration (malformed hash, unknown scope, duplicate names or keys) stops the app at startup.
- `GET /health` is anonymous; the Hangfire dashboard stays local-requests-only.
- To revoke a key, remove its entry and restart. To rotate, add a second entry with the **same `Name`** and the new
  hash, move the caller to the new key, then remove the old entry. Entries sharing a name are one client, so tasks
  queued under the old key stay readable with the new one.

## API

| Method | Path | Scope | Purpose |
|---|---|---|---|
| POST | `/api/classify` | `tasks` | Dry run: sensitivity, PII spans (no values) and routing decision |
| POST | `/api/tasks` | `tasks` | Enqueue an `LlmTask` as a Hangfire job → `{ payloadId, jobId }` |
| GET | `/api/tasks/{payloadId}` | `tasks` | Result (kept for 24h) or `pending`; own tasks only |
| POST | `/api/tasks/sync` | `tasks` | Run immediately (for debugging) |
| POST | `/api/consents` | `consent:write` | Record consent / legitimate use for a principal + purpose |
| DELETE | `/api/consents/{principal}/{purpose}` | `consent:write` | Withdraw |
| GET | `/api/consents/{principal}` | `consent:read` | List a principal's consent records |
| GET | `/api/audit?principalId=` | `audit:read` | Processing summary |
| GET | `/health` | none | Liveness |
| GET | `/hangfire` | local only | Dashboard |

Example:

```bash
curl -X POST localhost:5077/api/consents -H "X-Api-Key: $PLLM_KEY" -H "Content-Type: application/json"   -d '{"dataPrincipalId":"cust-42","purpose":"order-support","basis":"VoluntarilyProvided"}'

curl -X POST localhost:5077/api/tasks -H "X-Api-Key: $PLLM_KEY" -H "Content-Type: application/json" -d '{
  "taskId":"ord-1001","taskType":"address-normalization","purpose":"order-support","dataPrincipalId":"cust-42",
  "systemPrompt":"Normalise this Indian address to JSON with line1, locality, city, state, pincode.",
  "input":"flat 302 sai residency 14th cross indiranagar blr 560038 call 9876543210",
  "declaredFields":["address","phone"],"jsonMode":true }'
```
