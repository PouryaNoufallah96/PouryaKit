# PouryaKit

A reusable foundation for building .NET applications as modular monoliths made of vertical slices, so
that feature code holds only business behaviour.

## Language

### Structure

**Kit**:
The set of PouryaKit packages taken together.
_Avoid_: Framework, building blocks, foundation library

**Capability**:
One independently usable area of the Kit, delivered as one package or a small group of packages.
_Avoid_: Layer, building block

**Host**:
A deployable application that runs Operations, whether it composes Modules or none.
_Avoid_: API project, shell

**Module**:
A business boundary inside a Host that owns its data, its rules and its public Contracts.
_Avoid_: Service, bounded-context project, component

**Business Area**:
A cohesive part of a Module, centred on one aggregate or closely related group, that keeps its entities, data access, events and Features together.
_Avoid_: Aggregate folder, sub-module, domain

**Feature**:
One use case of a Business Area: a single command or query together with everything needed to serve it.
_Avoid_: Slice, use-case folder, endpoint

**Contracts**:
The public surface a Module offers to other Modules: the messages it publishes and the questions it answers.
_Avoid_: Shared kernel, common, API

**Composition Root**:
The one place where a Module declares everything it needs and everything it offers.
_Avoid_: Module registration, startup, installer

**Composition check**:
A check of a Host's whole composition that the Kit runs when the Host's container is built, before anything runs, such as a Module's declaration made without its Module.
_Avoid_: Health check, startup filter, startup check

**Startup check**:
A check of a Host that only the built application can answer, such as an endpoint it never mapped, which the Kit runs when the Host starts, before any hosted service starts.
_Avoid_: Health check, startup filter, composition check

**Gateway**:
The only entry point through which anything outside a Module asks it to run a command or query.
_Avoid_: Module facade, mediator, bus

**Gateway tool**:
A Module's command or query offered to a model as a tool: each call the model makes runs as an Operation through the Module's Gateway, continuing the Operation Context of the run that offered the tool. Built in `PouryaKit.AI`.
_Avoid_: Function, plugin, agent tool

**MCP tool**:
A tool of an MCP server the Host's AI options name, offered to a model beside its Gateway tools under the same allow-list and iteration cap: the call runs on that server, never as an Operation, and the Host, not the run, owns the server's client. Built in `PouryaKit.AI`.
_Avoid_: Plugin, external tool, remote function

**Reverse proxy**:
The deployable application in front of a system's Hosts that forwards each incoming HTTP request, by configured routes, to the Host that serves it; it runs no Operation. Built on YARP in `PouryaKit.ReverseProxy`.
_Avoid_: Gateway, API gateway, edge service

### Execution

**Operation**:
One command or query running inside one Module from entry to outcome.
_Avoid_: Request, call, transaction

**Operation Context**:
The facts about who and what caused an Operation, available wherever the Operation runs.
_Avoid_: User accessor, request context, execution context

**Actor**:
The person or system on whose authority an Operation runs.
_Avoid_: User, principal, caller

**Tenant**:
The customer or company an Operation runs for, carried on the Operation Context and taken only from a verified claim: at the HTTP boundary `PouryaKit.Web` reads it from the claim `PouryaKit.Security.JwtBearer` writes when it validates a token, and work an Operation causes continues its tenant. A tenant-scoped Module runs no Operation and applies no delivery without one, and its tenant-scoped aggregates are read and saved only by Operations of the tenant they belong to.
_Avoid_: Organization, company id, owner

**Request id**:
The id a caller gives one request, carried on the Operation Context. With the tenant, the Actor and the command it names the key an outermost idempotent command reserves in its Unit of Work, and a repeat of the request replays that command's first success from it.
_Avoid_: Idempotency token, dedup key, idempotency marker

**Unit of Work**:
The single owner of an Operation's outcome: everything it changed is kept together or discarded together.
_Avoid_: Transaction behaviour, save pipeline, writer

**Result**:
The outcome of an Operation or business rule: a value on success, one or more Errors on failure.
_Avoid_: Response, outcome, maybe

**Error**:
A named business reason an Operation did not succeed.
_Avoid_: Exception, failure, problem

### Events and messages

**Domain Event**:
A fact raised by an aggregate, visible only inside its Module.
_Avoid_: Notification, internal event

**Integration Event**:
A fact a Module publishes for other Modules, describing something that happened.
_Avoid_: External event, public event, message

**Snapshot**:
The current state of one aggregate published for other Modules, superseding every earlier Snapshot of it.
_Avoid_: State event, change event, projection message

**Tombstone**:
A Snapshot stating that the aggregate no longer exists.
_Avoid_: Delete event

**Domain Event Handler**:
A reaction to a Domain Event inside the same Module that takes part in the same Unit of Work.
_Avoid_: Event listener, internal subscriber

**Notification**:
A Domain Event that has been committed and is waiting to be reacted to outside the Unit of Work that raised it.
_Avoid_: Outbox message, deferred event, post-commit event

**Notification Handler**:
A reaction to a Notification that reaches outside the Module's own data: sending something, or publishing an Integration Event.
_Avoid_: Consumer, outbox processor, side-effect handler

**Integration Event Handler**:
A Module's reaction to an Integration Event or Snapshot published by another Module.
_Avoid_: Consumer, subscriber, listener

**Wire name**:
The stable name a Notification, Integration Event or Snapshot is stored under, and by which a delivery read back finds its type; an Integration Event or Snapshot is also sent between Hosts under it. A delivery's queue is its subscription's name, not the wire name. One wire name is one version of one contract type.
_Avoid_: Message type name, topic, routing key, alias

**Subscription**:
One Notification Handler or Integration Event Handler subscribed to one message type under a stable name, which names its own durable queue and keys its Receipts, so its deliveries are retried and set aside as dead letters on their own.
_Avoid_: Subscriber, listener, consumer group

**Service name**:
Wolverine's name for a Host on the broker: the Host's queue there is named by it, every instance of one kind of Host shares it, and two kinds of Host never do. The `serviceName` parameter keeps Wolverine's term.
_Avoid_: Host name, application name, queue name

**Delayed command**:
A command an Operation schedules to run later in its own Module, committed with the Operation and run once due as its continuation, with its Actor, tenant and correlation.
_Avoid_: Scheduled job, timer, deferred command

**Recurring command**:
A command a Module declares to run at every occurrence of a cadence, each occurrence an Operation of its own for the system Actor the declaration names, continuing nothing.
_Avoid_: Cron job, scheduled job, background job, timer

**Outbox**:
A Module's share of its Host's durable message store: the Notifications, Integration Events, Snapshots, delayed commands and recurring commands' next occurrences the Module has committed but not yet finished delivering.
_Avoid_: Message table, queue

**Inbox**:
A Module's share of its Host's durable message store: the Integration Events and Snapshots the Module has received but not yet applied.
_Avoid_: Queue, message table

**Stream**:
An aggregate whose stored state is the ordered events it has appended, loaded by folding them; the Kit's second write model, whose Unit of Work commits its events with the Operation's Notifications and Integration Events through Wolverine's Marten outbox (ADR 0011). A Stream belongs to the tenant of the Operation that wrote it.
_Avoid_: Event-sourced entity, event stream aggregate

**Saga**:
A Module's long-running reaction across Integration Events that keeps its own state between steps: started and advanced by Integration Events, timed out by its own timeout messages, and the one reaction that runs outside an Operation, each step with no Operation Context, claimed once in its own transaction, and emitting Integration Events under its system Actor (ADR 0013). A saga's identity is the tenant of the event that started it with that event's business id; one Module declares it with `AddPouryaKitSaga` and derives it from `PouryaKitSaga` (`PouryaKit.Messaging.Wolverine.Sagas`).
_Avoid_: Process manager, workflow, orchestration

**Page**:
One bounded slice of a read result, with its position and the total count.
_Avoid_: Paged list, list result, page list

**Receipt**:
A handler's record that it has already applied a specific Notification, Integration Event or Snapshot, or a Module's record that it has run a specific delayed command or occurrence of a recurring command.
The Module's Receipt store also holds its publication records, keyed `<wire name>#published`, one per Integration Event type and Domain Event; those are not Receipts (ADR 0006).
_Avoid_: Inbox row, processed flag, idempotency marker (a repeated request is recognised by its Request id, not by a Receipt)

**Channel**:
The name a Notification Handler pushes a realtime event on, and on which a browser client of the same tenant listens through a Module's SSE endpoint whose rule admits its Actor (ADR 0012). It is no Subscription and no queue: nothing is stored for it, and a client hears it only while connected.
_Avoid_: Topic, group, room

**Report job**:
One report an Actor requested from the report Module, stored with its requester and tenant, generated in the background by attempts that each claim it and store their file under a key of their own, and served within its tenant to its requester, or an Actor the Host grants the tenant's jobs, until its retention removes it. Built in `PouryaKit.Reporting.Module`.
_Avoid_: Report task, export job, report run

### Delivery of the Kit

**Wave**:
A group of Capabilities released together because later ones build on earlier ones.
_Avoid_: Milestone, release, version

**Phase**:
A step of work within a Wave that ends with stated behaviour proven by tests.
_Avoid_: Sprint, stage

**Spike**:
Throwaway code written only to answer a design question.
_Avoid_: Prototype, proof of concept

**Proving Sample**:
The sample application whose passing tests are the evidence that the Kit's guarantees hold.
_Avoid_: Demo, example, template

**Project template**:
A `dotnet new` template that generates a new application from the Proving Sample's business code in one composition, a monolith or a modular monolith.
_Avoid_: Starter, scaffold, boilerplate, sample
