#!/usr/bin/env python3
"""Read-only hosted main-job guard. Failure skips the privileged publication job."""

import argparse
import os

from artifacts import producer
from transport import Http, PublicationError


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--attempt", required=True)
    parser.add_argument("--source-sha", required=True)
    arguments = parser.parse_args()
    try:
        producer(Http(os.environ.get("PUBLICATION_TOKEN", "")), arguments.run_id,
                 arguments.attempt, arguments.source_sha)
    except (PublicationError, KeyError, TypeError, AttributeError) as error:
        print(str(error) if isinstance(error, PublicationError) else "Unsupported original run metadata.")
        return 1
    print("Original trusted main AI, package validation and publication protocol jobs succeeded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
