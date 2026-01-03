#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/publish"
RID="${1:-linux-x64}"

rm -rf "$OUT"
mkdir -p "$OUT"

publish_one () {
  local proj="$1"
  local name="$2"
  echo "Publishing $name ($RID)..."
  dotnet publish "$ROOT/$proj" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "$OUT/$name/$RID"
}

publish_one "src/SimpleEndpointBus.Broker/SimpleEndpointBus.Broker.csproj" "broker"
publish_one "src/SimpleEndpointBus.SampleService/SimpleEndpointBus.SampleService.csproj" "sample-service"
publish_one "src/SimpleEndpointBus.SampleSender/SimpleEndpointBus.SampleSender.csproj" "sample-sender"

echo ""
echo "Done."
echo "Output: $OUT"
