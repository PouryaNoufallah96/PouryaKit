#!/usr/bin/env bash
# Local reproduction of CI's three jobs on the checked-out commit while hosted runs are unavailable (docs/ci.md): every
# `run:` step of the `ai-setup` job, of the package validation job except the two evidence.py steps, which read
# the Actions run, and of the `website` job. Each step's full output is kept, so a failure can be read afterwards (Wave 3 session 5 lost one to a
# `tail -3`), and the PASS line carries the Release test totals, so nothing is lost when the evidence directory goes.
# Usage: .agents/scripts/ci-local.sh <evidence directory outside the checkout> [> <log>]. Never a CI result: report it
# as a local reproduction.
set -uo pipefail
evidence=${1:?usage: ci-local.sh <evidence directory outside the checkout>}
top=$(git rev-parse --show-toplevel)
cd "$top" || exit 1
echo "commit $(git rev-parse HEAD) $(date -u +%FT%TZ)"
echo "== ai-setup"
python3 .agents/scripts/check-ai-setup.py; echo "rc=$?"
python3 .agents/scripts/check-docs.py; echo "rc=$?"
python3 -m unittest discover -s .agents/scripts -p 'test_*.py'; echo "rc=$?"
echo "== build-and-test"
python3 -m unittest discover -s scripts/ci -p 'test_*.py'; echo "rc=$?"
python3 -m unittest discover -s scripts/packaging -p 'test_*.py'; echo "rc=$?"
rm -rf "$evidence" && mkdir -p "$evidence"
python3 scripts/packaging/validate.py --no-fetch --keep --output "$evidence" > "$evidence.log" 2>&1; rc=$?
grep -E '^(PASS|FAIL|Traceback|RuntimeError|ValueError)' "$evidence.log" | tail -5; echo "validate rc=$rc (full log: $evidence.log; evidence kept in $evidence until you remove it)"
echo "== website"
pnpm --dir website install --frozen-lockfile; echo "rc=$?"
pnpm --dir website check-licences; echo "rc=$?"
pnpm --dir website test; echo "rc=$?"
pnpm --dir website lint; echo "rc=$?"
pnpm --dir website build; echo "rc=$?"
pnpm --dir website covers; echo "rc=$?"
pnpm --dir website sync-public --check; echo "rc=$?"
echo "done $(date -u +%FT%TZ)"
