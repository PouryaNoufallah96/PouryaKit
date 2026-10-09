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
