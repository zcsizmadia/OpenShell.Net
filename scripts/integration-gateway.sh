#!/usr/bin/env bash
# SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
# SPDX-License-Identifier: Apache-2.0
#
# Start a real NVIDIA OpenShell gateway with the Docker compute driver, for the integration tests.
#
#   scripts/integration-gateway.sh <command> [args...]   start the gateway, run the command against it, stop it
#   scripts/integration-gateway.sh                       start the gateway, print the test variables, wait for Ctrl-C
#
# For example: scripts/ci.sh build && scripts/integration-gateway.sh scripts/ci.sh integration
#
# Linux only (x86_64 or aarch64), with Docker, curl, openssl, and python3. It mirrors upstream's
# e2e/with-docker-gateway.sh (host, non-container branch) at the tag in UPSTREAM_VERSION:
#   - downloads the openshell-gateway release binary for that tag and verifies its SHA-256 checksum;
#   - pulls ghcr.io/nvidia/openshell/{supervisor,sandbox}:<version> and the default sandbox image;
#   - generates the gateway PKI (`openshell-gateway generate-certs`) and the Ed25519 gateway JWT keys;
#   - starts the gateway on 127.0.0.1 with TLS and mTLS client authentication, and waits for /healthz.
#
# The command runs with OPENSHELL_GATEWAY_ADDRESS, OPENSHELL_CA_CERT, OPENSHELL_CLIENT_CERT, and
# OPENSHELL_CLIENT_KEY set (see tests/OpenShell.Sdk.IntegrationTests/GatewayFixture.cs).
#
# Environment overrides:
#   OPENSHELL_INTEGRATION_LOG_DIR      where the gateway log and, on failure, sandbox container logs are
#                                      written (default: TestResults/integration-gateway)
#   OPENSHELL_INTEGRATION_CACHE_DIR    where the gateway binary is downloaded (default: .cache/integration-gateway)
#   OPENSHELL_DOTNET_SDK_TEST_IMAGE    default sandbox image (default: nvcr.io/nvidia/base/ubuntu:24.04)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
tag="$(tr -d '[:space:]' < "$root/UPSTREAM_VERSION")"
version="${tag#v}"
registry="ghcr.io/nvidia/openshell"
supervisor_image="$registry/supervisor:$version"
sandbox_runtime_image="$registry/sandbox:$version"
sandbox_image="${OPENSHELL_DOTNET_SDK_TEST_IMAGE:-nvcr.io/nvidia/base/ubuntu:24.04}"
log_dir="${OPENSHELL_INTEGRATION_LOG_DIR:-$root/TestResults/integration-gateway}"
cache_dir="${OPENSHELL_INTEGRATION_CACHE_DIR:-$root/.cache/integration-gateway}"

die() {
  echo "ERROR: $*" >&2
  exit 2
}

[[ "$(uname -s)" == Linux ]] || die "the gateway runs on Linux only (Docker compute driver)"
case "$(uname -m)" in
  x86_64 | amd64) target="x86_64-unknown-linux-gnu" ;;
  aarch64 | arm64) target="aarch64-unknown-linux-gnu" ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac
for tool in docker curl openssl python3 sha256sum tar; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
done
docker info >/dev/null 2>&1 || die "the Docker daemon is not reachable (docker info failed)"

# ── Gateway binary ───────────────────────────────────────────────────
asset="openshell-gateway-$target.tar.gz"
release_url="https://github.com/NVIDIA/OpenShell/releases/download/$tag"
bin_dir="$cache_dir/$tag-$target"
gateway_bin="$bin_dir/openshell-gateway"
if [[ ! -x "$gateway_bin" ]]; then
  echo "Downloading $asset ($tag)..."
  download="$(mktemp -d)"
  curl -fsSL --retry 6 --retry-all-errors --retry-delay 10 --connect-timeout 30 -o "$download/$asset" "$release_url/$asset"
  curl -fsSL --retry 6 --retry-all-errors --retry-delay 10 --connect-timeout 30 -o "$download/checksums.txt" "$release_url/openshell-gateway-checksums-sha256.txt"
  expected="$(awk -v f="$asset" '$2 == f || $2 == "*" f { print $1 }' "$download/checksums.txt")"
  [[ -n "$expected" ]] || die "$asset is not listed in openshell-gateway-checksums-sha256.txt"
  actual="$(sha256sum "$download/$asset" | cut -d' ' -f1)"
  [[ "$expected" == "$actual" ]] || die "checksum mismatch for $asset: expected $expected, got $actual"
  mkdir -p "$download/extract" "$bin_dir"
  tar -xzf "$download/$asset" -C "$download/extract"
  found="$(find "$download/extract" -type f -name openshell-gateway | head -1)"
  [[ -n "$found" ]] || die "openshell-gateway not found in $asset"
  install -m 0755 "$found" "$gateway_bin"
  rm -rf "$download"
fi
"$gateway_bin" --version

# ── Images ───────────────────────────────────────────────────────────
pull() {
  local image=$1 attempt
  if docker image inspect "$image" >/dev/null 2>&1; then
    return 0
  fi
  for attempt in 1 2 3 4; do
    echo "Pulling $image (attempt $attempt)..."
    docker pull --quiet "$image" && return 0
    sleep 10
  done
  die "could not pull $image"
}
pull "$supervisor_image"
pull "$sandbox_runtime_image"
pull "$sandbox_image"

# ── Work directory, PKI, JWT keys, config ────────────────────────────
pick_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'
}
toml_string() {
  local value=$1
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '"%s"' "$value"
}

workdir="$(mktemp -d "${TMPDIR:-/tmp}/openshell-integration.XXXXXX")"
mkdir -p "$log_dir"
gateway_log="$log_dir/gateway.log"
: > "$gateway_log"
port="$(pick_port)"
health_port="$(pick_port)"
namespace="dotnet-it-$$-$port"
gateway_pid=""

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [[ -n "$gateway_pid" ]] && kill -0 "$gateway_pid" 2>/dev/null; then
    echo "Stopping openshell-gateway (pid $gateway_pid)..."
    kill "$gateway_pid" 2>/dev/null || true
    for _ in $(seq 1 60); do
      kill -0 "$gateway_pid" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$gateway_pid" 2>/dev/null || true
    wait "$gateway_pid" 2>/dev/null || true
  fi

  local ids id name
  ids="$(docker ps -aq --filter "label=openshell.ai/managed-by=openshell" \
    --filter "label=openshell.ai/sandbox-namespace=$namespace" 2>/dev/null || true)"
  if [[ "$status" -ne 0 && -n "$ids" ]]; then
    echo "Saving sandbox container logs to $log_dir..."
    for id in $ids; do
      name="$(docker inspect --format '{{.Name}}' "$id" 2>/dev/null | tr -d '/' || true)"
      {
        docker inspect --format '{{.Name}} state={{.State.Status}} exit={{.State.ExitCode}} restarts={{.RestartCount}} error={{.State.Error}}' "$id" || true
        docker logs "$id" 2>&1 || true
      } > "$log_dir/container-${name:-$id}.log" 2>&1
    done
  fi
  if [[ -n "$ids" ]]; then
    # shellcheck disable=SC2086
    docker rm -f $ids >/dev/null 2>&1 || true
  fi

  if [[ "$status" -ne 0 ]]; then
    echo "=== gateway log (last 200 lines; full log in $gateway_log) ==="
    tail -n 200 "$gateway_log" || true
    echo "=== end gateway log ==="
  fi
  rm -rf "$workdir"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

pki="$workdir/pki"
"$gateway_bin" generate-certs --output-dir "$pki" --server-san host.openshell.internal >/dev/null

jwt="$workdir/jwt"
mkdir -p "$jwt"
(umask 077 && openssl genpkey -algorithm Ed25519 -out "$jwt/signing.pem" >/dev/null 2>&1)
openssl pkey -in "$jwt/signing.pem" -pubout -out "$jwt/public.pem" >/dev/null 2>&1
openssl rand -hex 16 > "$jwt/kid"

config="$workdir/gateway.toml"
{
  printf '[openshell]\nversion = 2\n\n'
  printf '[openshell.gateway]\nlog_level = "info"\n'
  printf 'guest_tls_ca = %s\n\n' "$(toml_string "$pki/ca.crt")"
  printf '[openshell.gateway.gateway_jwt]\n'
  printf 'signing_key_path = %s\n' "$(toml_string "$jwt/signing.pem")"
  printf 'public_key_path = %s\n' "$(toml_string "$jwt/public.pem")"
  printf 'kid_path = %s\n' "$(toml_string "$jwt/kid")"
  printf 'gateway_id = %s\n\n' "$(toml_string "openshell-dotnet-it-$port")"
  printf '[openshell.gateway.mtls_auth]\nenabled = true\n\n'
  printf '[openshell.drivers.docker]\n'
  printf 'allow_driver_config = true\n'
  printf 'sandbox_label = %s\n' "$(toml_string "$namespace")"
  printf 'default_image = %s\n' "$(toml_string "$sandbox_image")"
  printf 'image_pull_policy = "if_not_present"\n'
  printf 'enable_bind_mounts = true\n'
  printf 'sandbox_runtime_image = %s\n' "$(toml_string "$sandbox_runtime_image")"
  printf 'supervisor_image = %s\n' "$(toml_string "$supervisor_image")"
  printf '\n[openshell.drivers.docker.resource_admission]\nenabled = false\n'
} > "$config"
cp "$config" "$log_dir/gateway.toml"

# ── Start and wait for health ────────────────────────────────────────
echo "Starting openshell-gateway $tag on 127.0.0.1:$port (namespace $namespace)..."
XDG_STATE_HOME="$workdir/state" XDG_CONFIG_HOME="$workdir/config" XDG_DATA_HOME="$workdir/data" \
  "$gateway_bin" \
  --config "$config" \
  --bind-address 127.0.0.1 \
  --port "$port" \
  --health-port "$health_port" \
  --compute-driver docker \
  --tls-cert "$pki/server/tls.crt" \
  --tls-key "$pki/server/tls.key" \
  --tls-client-ca "$pki/ca.crt" \
  --enable-websocket-tunnel true \
  --db-url "sqlite:$workdir/gateway.db?mode=rwc" \
  >> "$gateway_log" 2>&1 &
gateway_pid=$!

elapsed=0
until curl -sf "http://127.0.0.1:$health_port/healthz" >/dev/null 2>&1; do
  kill -0 "$gateway_pid" 2>/dev/null || { echo "ERROR: openshell-gateway exited before becoming healthy" >&2; exit 1; }
  [[ "$elapsed" -lt 120 ]] || { echo "ERROR: the gateway did not become healthy within 120s" >&2; exit 1; }
  sleep 2
  elapsed=$((elapsed + 2))
done
echo "Gateway healthy after ${elapsed}s."

export OPENSHELL_GATEWAY_ADDRESS="https://127.0.0.1:$port"
export OPENSHELL_CA_CERT="$pki/ca.crt"
export OPENSHELL_CLIENT_CERT="$pki/client/tls.crt"
export OPENSHELL_CLIENT_KEY="$pki/client/tls.key"
export OPENSHELL_DOTNET_SDK_TEST_IMAGE="$sandbox_image"

if [[ "$#" -eq 0 ]]; then
  cat <<EOF

The gateway is running. In another shell:

  export OPENSHELL_GATEWAY_ADDRESS='$OPENSHELL_GATEWAY_ADDRESS'
  export OPENSHELL_CA_CERT='$OPENSHELL_CA_CERT'
  export OPENSHELL_CLIENT_CERT='$OPENSHELL_CLIENT_CERT'
  export OPENSHELL_CLIENT_KEY='$OPENSHELL_CLIENT_KEY'
  export OPENSHELL_DOTNET_SDK_TEST_IMAGE='$OPENSHELL_DOTNET_SDK_TEST_IMAGE'

Press Ctrl-C to stop it.
EOF
  trap 'exit 0' INT
  wait "$gateway_pid"
  exit 1
fi

echo "Running against $OPENSHELL_GATEWAY_ADDRESS: $*"
"$@"
