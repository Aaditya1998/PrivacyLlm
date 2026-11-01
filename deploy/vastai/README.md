# Self-hosted tier on Vast.ai

## 1. Pick an offer

Vast.ai is a marketplace: the GPU box belongs to a third-party host. Under the DPDP Act that host is part of
your processing chain, so filter hard:

```bash
vastai search offers 'gpu_ram>=48 num_gpus=1 reliability>0.99 datacenter=true geolocation=IN inet_down>500' -o 'dph'
```

- `datacenter=true` keeps you on verified datacenter hosts rather than individual machines.
- `geolocation=IN` keeps personal data in India. The app enforces this too: `SelfHostedLlm:HostRegion` must be
  in `Routing:AllowedPersonalDataRegions`, or personal-data tasks are rejected.
- If no Indian offer fits, decide that deliberately (DPDP s.16 allows transfer except to notified countries)
  and record it. Don't let it happen by accident.

## 2. Launch

```bash
vastai create instance <OFFER_ID> \
  --image vllm/vllm-openai:latest \
  --disk 80 \
  --env "-p 8000:8000 -e VLLM_API_KEY=$VLLM_API_KEY" \
  --onstart deploy/vastai/start-vllm.sh
```

Pin the image to a specific tag in production instead of `latest`.

## 3. Connect the .NET app

Don't send prompts over plain HTTP on a mapped public port. Use one of:

- a WireGuard / Tailscale tunnel from the app host to the instance (recommended), or
- a Caddy/nginx sidecar terminating TLS in front of port 8000.

Then set:

```json
"SelfHostedLlm": { "BaseUrl": "https://<tunnel-or-host>:8000", "ApiKey": "<VLLM_API_KEY>", "HostRegion": "IN" }
```

## 4. Operate

- The `inference-health` Hangfire job polls `/health` every 5 minutes.
- When the instance is preempted, personal-data jobs **fail and retry** (5 attempts over ~19 min). They never
  fall back to the frontier API. Keep a second instance or an on-demand launch script ready if that retry
  window is too short for your workload.
- Destroy instances with `vastai destroy instance <id>` (not just stop) once you're done, so the disk holding
  the model cache and any swap is released.
