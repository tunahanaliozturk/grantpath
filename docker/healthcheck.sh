#!/bin/bash
# Container health check.
#
# The .NET runtime image ships neither curl nor wget, and installing a network client into a runtime image
# so it can ask itself one question is a package to keep patched forever. Bash is already there, and
# /dev/tcp is enough to make an HTTP request.
#
# A plain TCP connect would be simpler and would pass while the service returned 503 to everything, which
# is exactly the state a health check exists to notice.
set -euo pipefail

path="${1:-/health/live}"
port="${ASPNETCORE_HTTP_PORTS:-8080}"

exec 3<>"/dev/tcp/localhost/${port}"

printf 'GET %s HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' "${path}" >&3

# Only the status line matters. Reading the body would mean parsing a chunked response in bash.
head -1 <&3 | grep -q ' 200 '
