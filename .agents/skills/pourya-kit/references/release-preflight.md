# Deferred named-release automation

[#28/#29 are deferred](../../../../docs/releases-preflight.md) by the owner's 2026-10-06 scope adjustment. Their compatibility/progression engine, automated named cut and content-verified recovery are outside the current pipeline; the prior scripts and tests are removed.

Use the existing MinVer configuration and the owner-created tag followed by manual main CI path in [private package guidance](../../../../docs/private-packages.md#versions-and-triggers). Do not introduce a second version engine, claim compatibility approval from ordinary SDK package validation, or treat HTTP 409 duplicate skipping as identical-content proof. Resuming the original automation requires a new scoped ticket.
