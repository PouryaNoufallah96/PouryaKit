#!/usr/bin/env python3
"""Restore an exact trusted main artifact and run the shared private publication transaction."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import tempfile
import time

from artifacts import restore
from transport import FEED, REPOSITORY, Http, PublicationError
from publisher import publish


def execute(arguments, *, client_factory=Http):
    arguments.receipt.parent.mkdir(parents=True, exist_ok=True)
    receipt = {"repository": REPOSITORY, "feed": FEED, "complete": False, "result": "failure",
               "started_at": datetime.now(timezone.utc).isoformat(), "proof": arguments.proof}

    def save():
        temporary = arguments.receipt.with_suffix(".tmp")
        temporary.write_text(json.dumps(receipt, indent=2) + "\n")
        temporary.replace(arguments.receipt)

    save()
    try:
        client = client_factory(os.environ.get("PUBLICATION_TOKEN", ""))
        with tempfile.TemporaryDirectory(prefix="pouryakit-original-") as folder:
            original = restore(client, arguments.run_id, arguments.attempt, arguments.artifact_id,
                               arguments.source_sha, Path(folder), receipt)
            save()
            if arguments.proof == "hold":
                receipt["hold_seconds"] = 45
                save()
                time.sleep(45)
            if arguments.proof == "negative-content":
                manifest = json.loads((original / "manifest.json").read_text())
                file = original / "packages" / manifest["packages"][0]["file"]
                file.write_bytes(file.read_bytes() + b"proof-only mutation")
                manifest["packages"][0]["sha256"] = hashlib.sha256(file.read_bytes()).hexdigest()
                (original / "manifest.json").write_text(json.dumps(manifest))
            if arguments.proof == "negative-sha":
                path = original / "manifest.json"
                manifest = json.loads(path.read_text())
                manifest["commit"] = "0" * 40
                path.write_text(json.dumps(manifest))
            publish(original, arguments.source_sha, client, receipt, save,
                    fail_after_one=arguments.proof == "fail-after-one", probe=arguments.proof in ("probe", "negative-content"))
            if arguments.proof == "negative-content":
                raise PublicationError("Negative content proof requires conflicting existing bytes; the read-only probe made no uploads.")
        receipt["result"] = "probe-success" if arguments.proof == "probe" else "success"
        return 0
    except (PublicationError, OSError, KeyError, TypeError, AttributeError) as error:
        # Only controlled errors go into diagnostics; never exception text from network/filesystem libraries.
        receipt["error"] = str(error) if isinstance(error, PublicationError) else "Original evidence has an unsupported shape or cannot be read."
        print(f"FAIL: {receipt['error']}")
        return 1
    finally:
        receipt["finished_at"] = datetime.now(timezone.utc).isoformat()
        save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--attempt", required=True)
    parser.add_argument("--artifact-id", default="")
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--proof", choices=("none", "probe", "fail-after-one", "hold", "negative-content", "negative-sha"), default="none")
    return execute(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
