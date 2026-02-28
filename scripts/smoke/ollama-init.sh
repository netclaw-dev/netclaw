#!/bin/sh
set -eu

MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
ALT_MODEL="${SMOKE_OLLAMA_ALT_MODEL:-all-minilm:latest}"

echo "Waiting for Ollama API to become available..."
for i in $(seq 1 24); do
  if curl -fsS http://ollama:11434/api/tags >/dev/null; then
    echo "Ollama is ready."
    break
  fi

  echo "Ollama not ready yet (attempt $i/24)."
  sleep 5
done

echo "Pulling smoke model: $MODEL"
curl -fsS -X POST http://ollama:11434/api/pull -d "{\"name\":\"$MODEL\"}"

echo "Pulling alternate model for model-switch test: $ALT_MODEL"
curl -fsS -X POST http://ollama:11434/api/pull -d "{\"name\":\"$ALT_MODEL\"}"

echo "Verifying models are available..."
curl -fsS http://ollama:11434/api/tags

echo "Model init completed."
