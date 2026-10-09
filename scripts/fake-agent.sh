#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SAMPLE_STREAM="${SAMPLE_STREAM:-$ROOT_DIR/tests/fixtures/stream-json/sample.ndjson}"

if [[ ! -f "$SAMPLE_STREAM" ]]; then
  echo "error: sample stream file not found: $SAMPLE_STREAM" >&2
  exit 1
fi

mkdir -p bench
touch "bench/.fake-agent-edit.txt"
echo "fake edit $(date -u +"%Y-%m-%dT%H:%M:%SZ")" >> "bench/.fake-agent-edit.txt"

while IFS= read -r line; do
  printf "%s\n" "$line"
  sleep 0.03
done < "$SAMPLE_STREAM"
