#!/usr/bin/env bash
# Waits until no package validation and no .NET test run holds Docker on this machine and no waiter holds the machine's
# Docker lock. Given a command, it takes that lock, runs the command while holding it and exits with the command's status,
# so the next waiter's command starts only after this one ends.
#
# Usage: .agents/scripts/wait-docker-quiet.sh [max-minutes] [-- <command...>]
#   (default 60; exits 2 when the cap is reached before the command starts, 64 on a usage error)
#   .agents/scripts/wait-docker-quiet.sh 90 -- python3 scripts/packaging/validate.py --no-fetch --since <tip> --output <dir>
#
# One Docker host serves every implementer and validation of a session: PostgreSQL's 100-connection default fails
# concurrent suites with 53300, and two validations starve each other. A wait alone reserves nothing: two agents that see
# one suite end both start theirs, so the full suite and validate.py run under the lock. A process counts by what it
# runs, never by a line that names it: a shell that chains this script with its command (`sh -c "wait-docker-quiet.sh &&
# dotnet test ..."`) holds the same words, and matching such lines made two waiters block each other until the cap.
set -u
cap_minutes=60
if (( $# > 0 )) && [[ $1 != -- ]]; then
  cap_minutes=$1
  shift
fi
if (( $# > 0 )); then
  [[ $1 == -- ]] && (( $# > 1 )) || cap_minutes=usage
  shift
fi
if ! [[ $cap_minutes =~ ^[0-9]+$ ]]; then
  echo "usage: wait-docker-quiet.sh [max-minutes] [-- <command...>]" >&2
  exit 64
fi
deadline=$(( $(date +%s) + cap_minutes * 60 ))
# /tmp, not $TMPDIR, which macOS sets per user and an agent's sandbox per session: the lock is the machine's.
lock=/tmp/pourya-kit-docker.lock

# A command line as ps prints it, executable first: a Python running scripts/packaging/validate.py, `dotnet test`, or a
# VSTest host.
validation='^([^ ]*/)?[Pp]ython[0-9.]*( -[^ ]+)* ([^ ]*/)?scripts/packaging/validate\.py( |$)'
dotnet_test='^([^ ]*/)?dotnet test( |$)'
test_host='^([^ ]*/)?testhost( |$)|^([^ ]*/)?dotnet( [^ ]+)* [^ ]*testhost\.dll( |$)'
busy() {
  local command
  while read -r command; do
    if [[ $command =~ $validation || $command =~ $dotnet_test || $command =~ $test_host ]]; then
      return 0
    fi
  done < <(ps -Ao command=)
  return 1
}

owned=0
# True while another live waiter holds the lock. A lock without its PID is one being taken, whose holder writes the PID
# next; one whose holder died without its exit trap (a SIGKILL) is stale, and removed.
locked() {
  local holder
  [[ -d $lock ]] || return 1
  holder=$(cat "$lock/pid" 2>/dev/null)
  [[ -z $holder ]] && return 0
  (( owned )) && [[ $holder == "$$" ]] && return 1
  kill -0 "$holder" 2>/dev/null && return 0
  if [[ $(cat "$lock/pid" 2>/dev/null) == "$holder" ]]; then
    rm -f "$lock/pid"
    rmdir "$lock" 2>/dev/null
  fi
  return 1
}
release() {
  (( owned )) || return 0
  rm -f "$lock/pid"
  rmdir "$lock" 2>/dev/null
}
trap release EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Ready when Docker is quiet and the lock is free; with a command, ready once this waiter holds the lock and Docker is
# quiet, so a waiter that holds it waits out a suite run without the lock and reserves the next turn.
ready() {
  if (( $# == 0 )); then
    ! locked && ! busy
    return
  fi
  if (( ! owned )); then
    locked && return 1
    if ! mkdir "$lock" 2>/dev/null; then
      [[ -d $lock ]] || { echo "wait-docker-quiet: cannot create $lock" >&2; exit 1; }
      return 1
    fi
    owned=1
    echo $$ > "$lock/pid"
  fi
  ! busy
}

while ! ready "$@"; do
  if (( $(date +%s) >= deadline )); then
    echo "wait-docker-quiet: still busy after ${cap_minutes} minutes (lock: $lock)" >&2
    exit 2
  fi
  sleep 30
done
if (( $# == 0 )); then
  echo "wait-docker-quiet: Docker is quiet"
  exit 0
fi
echo "wait-docker-quiet: Docker is quiet; running $* under $lock"
"$@"
exit $?
