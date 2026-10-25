#!/usr/bin/env bash
# Waits until no package validation and no .NET test run holds Docker on this machine and no other waiter holds the
# machine's Docker lock. Given a command, it takes the lock as soon as the lock is free, waits for a quiet Docker while
# holding it, runs the command and exits with the command's status: the lock reserves the next turn, so the next waiter's
# command starts only after this one ends.
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
#
# The lock is a directory holding its holder's PID, which the holder's exit trap removes. It is stale when its holder no
# longer runs (killed, so the trap never ran), or when it holds no PID and is more than a minute old (its holder died
# between creating it and writing the PID); a younger one without a PID is still being taken. A waiter reclaims a stale
# lock by renaming it, which only one waiter can do, removes the renamed directory only if it still holds the PID judged
# dead, and puts back a lock taken again in between; a waiter that took the lock reads its PID back before it counts it.
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

mine() {
  [[ $(cat "$lock/pid" 2>/dev/null) == "$$" ]]
}

# Whether the lock directory $1, whose PID read $2 (empty for none), is stale. `ps -p` sees another user's or session's
# process, where `kill -0` fails with EPERM and would read it as dead.
stale() {
  if [[ -z $2 ]]; then
    [[ -n $(find "$1" -maxdepth 0 -mmin +1 2>/dev/null) ]]
  else
    ! ps -p "$2" >/dev/null 2>&1
  fi
}

# Removes the stale lock whose PID read $1; false when another waiter renamed it first or it was taken again since.
reclaim() {
  local moved="$lock.stale.$$.$RANDOM"
  mv "$lock" "$moved" 2>/dev/null || return 1
  if [[ $(cat "$moved/pid" 2>/dev/null) == "$1" ]]; then
    rm -f "$moved/pid"
    rmdir "$moved" 2>/dev/null
    return 0
  fi
  [[ -e $lock ]] || mv "$moved" "$lock" 2>/dev/null
  rm -f "$moved/pid"
  rmdir "$moved" 2>/dev/null
  return 1
}

# True while another waiter holds the lock or is taking it; a stale lock is reclaimed on the way.
locked() {
  local holder
  [[ -d $lock ]] || return 1
  holder=$(cat "$lock/pid" 2>/dev/null)
  [[ $holder == "$$" ]] && return 1
  stale "$lock" "$holder" || return 0
  reclaim "$holder" && return 1
  return 0
}

# Sourced, as the tests of the functions above source it, the script stops here.
[[ ${BASH_SOURCE[0]} == "$0" ]] || return 0

release() {
  mine || return 0
  rm -f "$lock/pid"
  rmdir "$lock" 2>/dev/null
}
trap release EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Ready when Docker is quiet and the lock is free; with a command, ready once this waiter holds the lock and Docker is
# quiet, so a waiter that holds it waits out a suite run without the lock and reserves the next turn.
ready() {
  local problem
  if (( $# == 0 )); then
    ! locked && ! busy
    return
  fi
  if ! mine; then
    locked && return 1
    if ! problem=$(LC_ALL=C mkdir "$lock" 2>&1); then
      [[ $problem == *"File exists"* ]] || { echo "wait-docker-quiet: cannot create $lock: $problem" >&2; exit 1; }
      return 1
    fi
    echo $$ > "$lock/pid" 2>/dev/null
    mine || return 1
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
