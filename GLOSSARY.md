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
The single deployable application that composes Modules.
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

**Gateway**:
The only entry point through which anything outside a Module asks it to run a command or query.
_Avoid_: Module facade, mediator, bus

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

**Delayed command**:
A command an Operation schedules to run later in its own Module, committed with the Operation and run once due as its continuation, with its Actor, tenant and correlation.
_Avoid_: Scheduled job, timer, deferred command

**Outbox**:
A Module's share of its Host's durable message store: the Notifications, Integration Events, Snapshots and delayed commands the Module has committed but not yet finished delivering.
_Avoid_: Message table, queue

**Inbox**:
A Module's share of its Host's durable message store: the Integration Events and Snapshots the Module has received but not yet applied.
_Avoid_: Queue, message table

**Stream**:
An aggregate whose stored state is the ordered events it has appended, loaded by folding them; the Kit's second write model.
_Avoid_: Event-sourced entity, event stream aggregate

**Page**:
One bounded slice of a read result, with its position and the total count.
_Avoid_: Paged list, list result, page list

**Receipt**:
A handler's record that it has already applied a specific Notification, Integration Event or Snapshot, or a Module's record that it has run a specific delayed command.
The Module's Receipt store also holds its publication records, keyed `<wire name>#published`, one per Integration Event type and Domain Event; those are not Receipts (ADR 0006).
_Avoid_: Inbox row, idempotency marker, processed flag

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
