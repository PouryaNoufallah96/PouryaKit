# The kit owns its dispatcher and takes no mediator dependency

Commands and queries are dispatched by a small dispatcher inside the kit with a fixed pipeline, not by
MediatR. MediatR 13+ is offered under RPL-1.5 or a commercial agreement that restricts redistribution
inside frameworks and wrappers, and the kit's policy is to take no runtime dependency on a reciprocal
or commercially licensed package. Wolverine's own mediator was rejected because it has no
open-generic handlers and no `Result`-aware rollback.

## Considered Options

- MediatR 12.5.0 (last Apache release): frozen, unsupported.
- martinothamar `Mediator` (MIT, source-generated): viable; revisit only if the owned dispatcher proves costly.
