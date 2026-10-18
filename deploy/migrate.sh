#!/bin/sh
set -eu
# The connection stays out of command-line arguments and image layers.
export ConnectionStrings__TaskFlow="$(cat /run/secrets/ConnectionStrings__TaskFlow)"
exec /app/efbundle
