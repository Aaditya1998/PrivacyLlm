#!/usr/bin/env bash
# On-start script for a Vast.ai instance (image: vllm/vllm-openai).
# Serves Qwen3 30B-A3B (MoE, ~3B active params) behind an API key.
#
# GPU sizing:
#   FP8 weights  (~31 GB) -> 1x 48 GB (L40S / A6000) with ~16k context, or 1x 80 GB (A100/H100) with 32k+
#   BF16 weights (~61 GB) -> 1x 80 GB (A100/H100), or 2x 48 GB with TENSOR_PARALLEL=2
#
# Required env (set as Vast.ai template env vars, never bake into the image):
#   VLLM_API_KEY   shared secret; the .NET app sends it as a Bearer token
# Optional:
#   MODEL_ID       default Qwen/Qwen3-30B-A3B-Instruct-2507-FP8
#   MAX_MODEL_LEN  default 32768
#   TENSOR_PARALLEL default 1
set -euo pipefail

: "${VLLM_API_KEY:?VLLM_API_KEY must be set}"
MODEL_ID="${MODEL_ID:-Qwen/Qwen3-30B-A3B-Instruct-2507-FP8}"
MAX_MODEL_LEN="${MAX_MODEL_LEN:-32768}"
TENSOR_PARALLEL="${TENSOR_PARALLEL:-1}"

# Privacy hardening:
#  - --disable-log-requests: vLLM otherwise logs prompt previews to stdout (which Vast.ai keeps in instance logs)
#  - HF_HUB_DISABLE_TELEMETRY / VLLM_NO_USAGE_STATS / DO_NOT_TRACK: no outbound usage reporting
export HF_HUB_DISABLE_TELEMETRY=1 VLLM_NO_USAGE_STATS=1 DO_NOT_TRACK=1

exec vllm serve "$MODEL_ID" \
  --served-model-name qwen3-30b-a3b \
  --host 0.0.0.0 --port 8000 \
  --api-key "$VLLM_API_KEY" \
  --max-model-len "$MAX_MODEL_LEN" \
  --tensor-parallel-size "$TENSOR_PARALLEL" \
  --gpu-memory-utilization 0.90 \
  --enable-prefix-caching \
  --disable-log-requests
