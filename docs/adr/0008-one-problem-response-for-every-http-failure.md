---
status: accepted
---

# Every HTTP failure is one problem response, and the most severe Error decides its status

`PouryaKit.Web` answers every failure with the same RFC 9457 problem response, written as `application/problem+json`
whatever the caller's `Accept` says, so a client parses one shape. Ticket #14 fixed it for a failed `Result` and an
exception, and ticket #19 extended it to the failures ASP.NET Core answers itself. A client reads the status and, for a
failed `Result`, the `errors` member; exception and empty-body responses omit `errors`. Changing that contract breaks
consumers of the Kit's HTTP endpoints. No ADR owned the HTTP boundary,
so these decisions are recorded here.

- **The most severe Error kind decides the status.** Each kind has one status: validation 400, not found 404,
  conflict 409, forbidden 403, anything else 500. When a failed `Result` carries Errors of several kinds, the most
  severe decides, in the order unexpected, forbidden, not found, conflict, validation: the caller cannot succeed by
  correcting its input while a more severe reason stands. The `errors` member still lists every Error, in order
  (`src/PouryaKit.Web/ResultHttpExtensions.cs`;
  `WebTests.Errors_of_several_kinds_answer_with_the_most_severe_kinds_status_and_list_every_error`).
- **`Error` carries an optional `Target`.** A validation Error may name the input it is about, and each entry of
  `errors` carries its `code`, its `message` and, when set, its `target`; every other Error has none
  (`src/PouryaKit.Domain/Error.cs`;
  `WebTests.An_empty_id_and_an_empty_name_answer_400_listing_every_validation_failure_and_nothing_is_saved`).
- **Empty-body failures are answered as problem responses.** A failure ASP.NET Core answers with an empty body, before
  any endpoint or exception handler runs, gets the same response with its status and no `errors`: routing's 405 and
  415, and the 401, 403 or 404 that authentication, authorization or routing answers. `AddPouryaKitWeb` adds this
  outermost in the pipeline; a response that already has a body is left alone
  (`src/PouryaKit.Web/ProblemStatusCodePages.cs`;
  `WebTests.A_request_routing_refuses_answers_its_status_as_a_problem_response_and_nothing_is_saved` for 405 and 415,
  `WebTests.A_module_endpoint_refuses_an_anonymous_caller_and_a_public_endpoint_runs_it_as_its_named_actor` for 401;
  `WebTests.Each_modules_endpoints_are_mapped_under_its_own_route_group` for 404;
  `WebTests.An_authenticated_caller_denied_authorization_answers_403_as_a_problem_response` for 403).

## Amendment (2026-10-07, ticket #36: the rate limiter's 429)

The decision above stands. Since #36 (2026-10-07) the rate limiter's 429 is answered the same way; it carries `title`
and `status` but no `type`, which ASP.NET Core defines for no 429
(`WebDefaultsTests.Each_actor_has_its_own_partition_which_spoofed_host_and_forwarded_for_headers_cannot_move_and_its_exhaustion_answers_429_as_a_problem_response`).
Moved under this heading on 2026-10-08 (#54), word for word, from the third bullet above.

## Amendment (2026-10-10, ticket #86: the Kit's reason as `detail`)

The decision above stands. A request that could not be read answers its own status, 400, without `errors`; until ticket
#86 it had no `detail` either, so the reason the Kit refused it, written in its message and logged at Debug, never
reached the caller. Since #86 the Kit raises its own refusals as the internal `KitBadRequestException`, a
`BadHttpRequestException`, and `UnexpectedExceptionHandler` writes its message as the problem's `detail`, RFC 9457's
member for a human-readable explanation: the rule broken by a page query string (`HttpPageRequest`), by a missing or
unreadable `Idempotency-Key`, or by a trusted peer's incomplete or unreadable context headers. A 400 ASP.NET Core raises
itself, such as malformed JSON, a body that names one member twice or a route value that does not parse, keeps no
`detail`, because its message quotes what the caller sent. No other member changes.

Pinned by `PagingTests.A_page_query_string_that_cannot_be_read_answers_400_as_a_problem_response`,
`IdempotencyTests.A_request_without_a_readable_idempotency_key_answers_400_as_a_problem_response_and_creates_nothing`,
`IdempotencyTests.An_idempotent_put_or_delete_without_a_key_answers_400_and_with_one_replays_the_first_response` and
`TrustedPeerTests.A_trusted_peer_whose_context_headers_are_incomplete_or_unreadable_is_refused_with_400`, each red when
the refusing sites threw the bare `BadHttpRequestException`, and by
`WebTests.A_request_that_cannot_be_read_answers_400_as_a_problem_response_and_is_not_logged_as_an_error`, red when the
handler wrote every such message.

## Amendment (2026-10-18, ticket #134: one failure shape per transport)

The decision above stands for HTTP. Since #134 a Kit Host also answers over gRPC (`PouryaKit.Grpc`), and each
transport has one failure shape of its own: HTTP the problem response above, gRPC a status. A failed `Result` answers
the status of its most severe Error kind, in the order above: validation `InvalidArgument`, not found `NotFound`,
conflict `Aborted`, forbidden `PermissionDenied`, anything else `Internal`, with the deciding Error's message as the
status's message. Every Error, in order, is in the binary trailer `pouryakit-errors-bin`, a JSON array in the Kit's
representation (`UsePouryaKitJson`) whose entries are the problem response's `errors` entries: `code`, `message` and,
when set, `target`. The deciding Error and the entry are Web's own (`ResultHttpExtensions`), which `PouryaKit.Grpc`
reuses, so the two transports cannot rank or list Errors apart. The service raises the status as gRPC's
`RpcException`, the transport's own way to answer one, from `ToGrpcResponse`
(`src/PouryaKit.Grpc/Services/ResultGrpcExtensions.cs`). A trusted peer's context headers the Kit refuses as
unreadable, which answer a 400 problem response with the rule as `detail` over HTTP (#86 above), answer
`InvalidArgument` with the rule as the status's message, before the service runs
(`src/PouryaKit.Grpc/Services/GrpcEndpointRouteBuilderExtensions.cs`).

Pinned by `GrpcTests.The_most_severe_error_kind_decides_the_status_as_it_decides_the_http_status`, red with only the
first Error in the trailer (and, before the review moved the ranking to Web's one, with forbidden ranked below not
found in the package's copy); `GrpcTests.A_failed_result_answers_its_errors_kind_with_every_error_in_the_trailer`; and
`GrpcTests.A_trusted_peers_unreadable_context_headers_answer_invalid_argument_with_the_rule`, red without the gRPC
group's refusal convention.
