#!/bin/sh
set -eu
# Only Render's public HTTP service edge exposes this port. Never publish it directly.
: "${ConnectionStrings__TaskFlow:?Set the private runtime database connection in Render secret settings}"
: "${DataProtection__ReadOnlyKeyRingBase64:?Set the persistent private key ring in Render secret settings}"
: "${RENDER_EXTERNAL_HOSTNAME:?A Render managed hostname is required}"
case "$RENDER_EXTERNAL_HOSTNAME" in
  *[!a-zA-Z0-9.-]*|'') echo 'Invalid Render hostname' >&2; exit 1 ;;
  *.onrender.com) ;;
  *) echo 'Only the managed Render hostname is supported by this free profile' >&2; exit 1 ;;
esac
taskflow_port="${PORT:-10000}"
case "$taskflow_port" in *[!0-9]*|'') echo 'Invalid port' >&2; exit 1 ;; esac
if [ "$taskflow_port" -lt 1 ] || [ "$taskflow_port" -gt 65535 ]; then echo 'Invalid port' >&2; exit 1; fi
# Environment secrets are provided by Render; do not use Compose's /run/secrets default.
unset TASKFLOW_SECRETS_DIRECTORY
export ASPNETCORE_URLS="http://0.0.0.0:$taskflow_port"
export ASPNETCORE_ENVIRONMENT=Production
export Transport__RequireHttps=true
export Hosting__RenderEdge=true
export AllowedHosts="$RENDER_EXTERNAL_HOSTNAME;healthcheck.render.com"
exec dotnet TaskFlow.Api.dll
