#!/usr/bin/env bash
# Waits until no package validation and no .NET test run holds Docker on this machine, then exits 0.
#
# Usage: .agents/scripts/wait-docker-quiet.sh [max-minutes]   (default 60; exits 2 when the cap is reached)
#
# One Docker host serves every implementer and validation of a session: PostgreSQL's 100-connection default fails
# concurrent suites with 53300, and two validations starve each other. The brief's earlier waiter, `pgrep -fl validate.py`,
# matched the waiting agent's own shell line (it holds the string) and never cleared. This one matches the processes
# themselves: the validation's python process and the .NET test hosts.
set -u
cap_minutes="${1:-60}"
deadline=$(( $(date +%s) + cap_minutes * 60 ))
# The patterns live in this file, so the waiting agent's own command line never holds them.
busy() {
  pgrep -f 'scripts/packaging/validate\.py' >/dev/null && return 0
  pgrep -x testhost >/dev/null && return 0
  pgrep -f 'testhost\.dll' >/dev/null && return 0
  pgrep -f 'dotnet test ' >/dev/null && return 0
  return 1
}
while busy; do
  if (( $(date +%s) >= deadline )); then
    echo "wait-docker-quiet: still busy after ${cap_minutes} minutes" >&2
    exit 2
  fi
  sleep 30
done
echo "wait-docker-quiet: Docker is quiet"
