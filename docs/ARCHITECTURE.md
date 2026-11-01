# Privacy-first LLM architecture for personal-data workloads

## Problem

Order-processing and customer-support jobs in a .NET 8 / Hangfire pipeline need LLM help: normalising messy
Indian addresses, drafting replies to customers, pulling fields out of free-text notes. Those payloads contain
names, phone numbers, addresses and sometimes PAN, UPI or Aadhaar numbers. Sending them to a third-party frontier
API means a cross-border transfer and another processor in the chain under India's Digital Personal Data
Protection Act, 2023. Most other LLM work (product copy, internal summaries, code) has no personal data and is
better served by frontier models.

## Design

```
 Hangfire job ──► ProtectedPayloadStore (Data Protection-encrypted, TTL)
      │                 ▲ only an opaque id is stored in Hangfire
      ▼                 │
 LlmTaskJob ──► PrivacyGateway
                  1. DataClassifier   regex detector (+ Verhoeff for Aadhaar) + declared schema fields
                  2. Lawful basis     ConsentRegistry: consent / voluntarily-provided, per purpose
                  3. RoutingPolicy    Public → Frontier (fallback: self-hosted)
                                      Personal → Self-hosted, NO fallback
                                      HighRisk (PAN/Aadhaar/bank/UPI) → Self-hosted, NO fallback
                                      Personal + approved task type → Frontier, pseudonymised
                  4. Region check     self-hosted host region ∈ AllowedPersonalDataRegions
                  5. Inference        VllmClient (Qwen3 on Vast.ai) │ FrontierClient (Anthropic API)
                  6. Post-process     strip <think>…</think>, rehydrate pseudonyms
                  7. Audit            one event per task: hash, PII counts, tier, model, region, outcome
```

### Invariants (covered by tests)

| Invariant | Test |
|---|---|
| Raw personal data never reaches the frontier client | `Frontier_never_sees_personal_data_even_when_self_hosted_is_down` |
| Personal-data tasks fail closed (Hangfire retries) instead of falling back | same |
| No lawful basis or withdrawn consent → no processing | `Missing_or_withdrawn_consent_blocks_processing` |
| Self-hosted tier outside allowed region → rejected | `Self_hosted_outside_allowed_region_is_rejected_for_personal_data` |
| Pseudonymised frontier path never sends names or phone numbers, and output is rehydrated | `Approved_task_is_pseudonymised_for_frontier_and_rehydrated` |
| Audit log holds no raw values | `Qwen_think_blocks_are_stripped_and_audit_holds_no_raw_text` |

### Why the detector is recall-biased

A false positive costs one self-hosted call instead of a frontier call. A false negative leaks personal data.
So: callers declare schema fields (`customer_name`, `phone`) and that alone marks the payload personal; regex
timeouts fail closed; pseudonymisation is opt-in per task type and never applies to high-risk identifiers.

## DPDP Act mapping

| Provision | Implementation |
|---|---|
| s.4–7 lawful processing (consent / legitimate use) | `IConsentRegistry`, checked before any personal-data inference |
| s.6(4) withdrawal as easy as consent | `DELETE /api/consents/{principal}/{purpose}`; takes effect on the next task |
| s.8(5) reasonable security safeguards | encrypted payload store, API-keyed vLLM behind a tunnel, `--disable-log-requests` |
| s.8(7) erase when purpose served | payload deleted after processing; results expire after 24h; hourly purge job |
| s.11 right to a summary of processing | `GET /api/audit?principalId=…` |
| s.16 cross-border transfer | `AllowedPersonalDataRegions` + Vast.ai `geolocation=IN` filter |

This is engineering support for compliance, not legal advice. Have counsel review purposes, notices and the
retention periods.

## Model evaluation (open weights for the personal-data tier)

Criteria, in priority order:

1. **Licence**: commercial use without per-user caps (Apache 2.0 preferred).
2. **Task quality** on our workloads: Indian address normalisation (mixed Hindi/English, landmarks), field
   extraction to JSON, short customer-reply drafting in English and Hinglish.
3. **Serving cost** on one rented GPU: tokens/sec at our concurrency, VRAM headroom for 16–32k context.
4. **Structured output reliability**: valid JSON rate with `response_format`.

| Candidate | Licence | Fit on 1 GPU | Notes |
|---|---|---|---|
| **Qwen3-30B-A3B-Instruct (FP8)**: selected | Apache 2.0 | 48 GB+ | MoE, ~3B active params → high throughput per GPU; strong multilingual; hybrid-thinking variants can be switched off per request |
| Qwen3-32B (dense) | Apache 2.0 | 80 GB (BF16) / 48 GB (FP8) | Slightly stronger reasoning, much lower throughput than the MoE |
| Llama 3.3 70B | Llama Community | 2×80 GB or heavy quant | Licence terms and VRAM cost rule it out for a single rented GPU |
| Gemma 3 27B | Gemma terms | 48 GB | Good quality; licence has use restrictions to review |

Re-run the comparison with your own labelled set before switching models: keep 200–500 real (consented,
then pseudonymised) samples per task, score exact-match on extracted fields and human-rate replies.

## Deployment notes

- `ProtectedPayloadStore` is in-memory in this reference build. With multiple app instances or Hangfire on SQL
  Server, back it with a shared store (SQL table or Redis) and persist the Data Protection key ring.
- The consent registry and audit log are in-memory too; swap in durable implementations of `IConsentRegistry`
  and `IAuditLog`.
- Frontier keys belong in a secret store (Key Vault / user-secrets), not `appsettings.json`.
