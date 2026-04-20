# LocalServiceBus — Design Document

**Status:** Implemented and deployed (v1.0)
**Last updated:** April 2026
**Audience:** Engineers maintaining the emulator, platform teams evaluating similar approaches

This document explains how the emulator was designed, what alternatives were considered and rejected, and the protocol-level problems that were solved during implementation. It is intentionally long because the "why" behind each decision is more valuable than the "what" — the what is in the source code.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Goals and Non-Goals](#2-goals-and-non-goals)
3. [Options Considered](#3-options-considered)
4. [Selected Approach](#4-selected-approach)
5. [High-Level Architecture](#5-high-level-architecture)
6. [Implementation Phases](#6-implementation-phases)
7. [Technical Challenges and Resolutions](#7-technical-challenges-and-resolutions)
8. [Trade-offs and Known Limitations](#8-trade-offs-and-known-limitations)
9. [Testing Strategy](#9-testing-strategy)
10. [References](#10-references)

---

## 1. Problem Statement

The organisation runs a .NET application that relies heavily on Azure Service Bus for asynchronous messaging. There are **~30 Azure Functions** with `[ServiceBusTrigger]` attributes and **many queues and topics** used by internal services.

Developers need to test code locally, but the current options all have significant pain points:

| Option | Pain |
|---|---|
| Test against a real Azure Service Bus in a dev subscription | Requires internet, shared state, cost, cross-team contention, slow CI |
| Use `Microsoft.Extensions.Azure` in-memory fakes | Cannot trigger `[ServiceBusTrigger]` — Functions never fire |
| Use MassTransit in-memory bus | Same limitation — not compatible with Functions bindings |
| Use the official Microsoft Service Bus Emulator | Requires Docker Desktop, which requires management approval |
| Use .NET Aspire | Requires Docker Desktop + partial tech-stack coverage (won't help .NET Framework 4.8 code) |

The `[ServiceBusTrigger]` constraint is the dominant one. The Functions runtime opens an **AMQP 1.0 connection** to a Service Bus endpoint and polls for messages via receiver links. Anything short of speaking real AMQP is invisible to the trigger binding.

## 2. Goals and Non-Goals

### Goals

- **Zero code changes** in developer applications and Functions — same SDK, same connection string style
- **No Docker dependency** — runs as a native .NET process
- **No Azure subscription** required
- **Wire-protocol fidelity** — real AMQP 1.0, not a mock
- **Runs on Windows x64, ARM64, and macOS** (any platform .NET 8 supports)
- **Support both current and legacy SDKs** — `Azure.Messaging.ServiceBus` v7+ and `Microsoft.Azure.ServiceBus` v4
- **Works with both isolated-worker and in-process Functions**, including .NET Framework 4.8
- **Easy developer installation** — `dotnet tool install --global localbus` and done

### Non-Goals (v1.0)

- Session-based messaging (FIFO sessions, session state)
- Message scheduling (`ScheduledEnqueueTime`)
- SQL filters / rules on subscriptions
- Message auto-forwarding
- TLS / AMQPS (localhost only, plain TCP)
- Geo-replication / federation
- Durable persistence by default (SQLite store is opt-in)
- Production use — this is a dev/test tool

## 3. Options Considered

Six options were evaluated during the planning phase. Each was scored on developer experience, fidelity to production, licensing cost, approval burden, and tech-stack coverage (.NET 8 vs .NET Core 3.1 vs .NET Framework 4.8).

### 3.1 Microsoft Official Service Bus Emulator (Docker)

- **Pros:** Official, high fidelity, actively maintained by Microsoft
- **Cons:** Requires Docker Desktop licence approval; a 12-15 person team running Docker Desktop triggers commercial licensing; developer machines would need Docker configured correctly
- **Verdict:** Blocked by licensing/approval burden

### 3.2 .NET Aspire with Containerised Service Bus

- **Pros:** Modern, integrates with the Functions host
- **Cons:** Requires Docker Desktop (same issue as above); tightly coupled to .NET 8 — the .NET Core 3.1 and .NET Framework 4.8 parts of the stack cannot use it
- **Verdict:** Only covers part of the stack; still needs Docker

### 3.3 Third-Party Cloud Emulators (e.g. LocalStack variants)

- **Pros:** Some support Service Bus-like semantics
- **Cons:** Most mature emulators focus on AWS, not Azure; those that do support Azure typically require Docker; paid tiers for commercial use
- **Verdict:** Gaps in Azure coverage

### 3.4 In-Process `MessageSender` Fakes (`Microsoft.Extensions.Azure` mocks)

- **Pros:** Fast, zero infrastructure
- **Cons:** Cannot trigger `[ServiceBusTrigger]`. The Functions runtime subscribes via AMQP — you cannot intercept that without speaking AMQP yourself. **This was the deal-breaker.**
- **Verdict:** Useless for the 30+ Functions in scope

### 3.5 MassTransit In-Memory Bus

- **Pros:** Mature, well-documented
- **Cons:** Same limitation as 3.4 — Functions bindings don't see MassTransit messages; would require replacing all `[ServiceBusTrigger]` with MassTransit handlers, which is a massive rewrite
- **Verdict:** Would invalidate the goal of "zero code changes"

### 3.6 Custom AMQP 1.0 Listener (Build Our Own)

- **Pros:** No Docker, no licensing, no rewrite, full control, supports all SDK versions, supports all Functions worker models, supports every .NET target framework the Service Bus SDK itself supports
- **Cons:** Non-trivial to implement — requires understanding Service Bus's AMQP extensions (CBS, `$management`, message annotations, lock tokens in delivery tags)
- **Verdict:** **Selected** — the only option that satisfies all goals

## 4. Selected Approach

**Build a custom Azure Service Bus emulator as a `dotnet tool` that speaks real AMQP 1.0 using [AMQPNetLite](https://github.com/Azure/amqpnetlite) as the wire-level implementation.**

### Why AMQPNetLite

- Maintained by Microsoft themselves
- Cross-platform, fully managed — no native dependencies, works on ARM64
- Provides a `ContainerHost` for the listener side (exactly what an emulator needs)
- Apache 2.0 licensed
- Ships on NuGet with a stable public API

### Why `dotnet tool` for distribution

- One command to install (`dotnet tool install --global LocalServiceBus`)
- No admin rights required (installs to `%USERPROFILE%\.dotnet\tools`)
- Automatic PATH integration
- Familiar to any .NET developer — same mechanism as `dotnet-ef`, `dotnet-format`, etc.
- Easy to publish to an internal NuGet feed for team-wide distribution

### Why .NET 8

- Latest LTS at the time of design
- Native ARM64 support (Parallels VMs, Apple Silicon)
- Best `System.Threading.Channels` perf (used for FIFO queue storage)
- Latest `System.CommandLine` preview (used for the CLI)
- The tool targets .NET 8 — but the *clients* of the emulator can be any target framework the Service Bus SDK supports, including .NET Framework 4.8

## 5. High-Level Architecture

```
┌─────────────────────────────────────────────────────┐
│               LocalServiceBus Emulator              │
│                                                     │
│  ┌──────────────────┐      ┌───────────────────┐    │
│  │  AMQP 1.0 Layer  │      │  REST API         │    │
│  │  port 5672       │      │  port 9090        │    │
│  │                  │      │                   │    │
│  │  LinkProcessor   │      │  ManagementApi    │    │
│  │  ├ BrokerSink    │      │  (Minimal API)    │    │
│  │  └ BrokerSource  │      └────────┬──────────┘    │
│  └────────┬─────────┘               │               │
│           │                         │               │
│           └────────────┬────────────┘               │
│                        ▼                            │
│           ┌────────────────────────┐                │
│           │     MessageBroker      │                │
│           │                        │                │
│           │  ┌──────────────────┐  │                │
│           │  │   QueueEngine    │  │                │
│           │  │  Channel<T> FIFO │  │                │
│           │  │  LockManager     │  │                │
│           │  │  DeadLetterQueue │  │                │
│           │  └──────────────────┘  │                │
│           │                        │                │
│           │  ┌──────────────────┐  │                │
│           │  │   TopicEngine    │  │                │
│           │  │  Fan-out         │  │                │
│           │  │  └ QueueEngine   │  │                │
│           │  │    per sub       │  │                │
│           │  └──────────────────┘  │                │
│           └────────────────────────┘                │
└─────────────────────────────────────────────────────┘
          ▲                        ▲
          │ AMQP 1.0               │ HTTP
          │                        │
   ┌──────┴───────┐       ┌────────┴───────┐
   │ Azure        │       │  localbus CLI  │
   │ Functions    │       │  curl / Postman│
   │ [SBTrigger]  │       │  Test scripts  │
   └──────────────┘       └────────────────┘
```

### Layered responsibilities

| Layer | Project | Responsibility |
|---|---|---|
| Protocol | `LocalServiceBus.Amqp` | AMQP 1.0 listener, link attach handling, SASL negotiation, CBS auth, `$management` RPC, delivery-tag injection |
| Core engine | `LocalServiceBus.Core` | Queue FIFO semantics, peek-lock, dead-lettering, topic fan-out, lock expiry — pure in-process logic, zero AMQP knowledge |
| Management | `LocalServiceBus.Management` | REST API for inspection, test injection, reset |
| Host | `LocalServiceBus.Host` | CLI entry point (`System.CommandLine`), config file loader, `dotnet tool` packaging |

The **`Core` engine has no dependency on AMQP**. This is intentional — the engine could be driven by any protocol front-end (HTTP, gRPC, direct in-process). All AMQP-specific behaviour lives in the `Amqp` project behind a clean `MessageBroker` facade.

## 6. Implementation Phases

The work was divided into four phases to ensure a working vertical slice at each milestone.

### Phase 1 — Core Engine

In-process broker with no protocol layer. Unit-tested in isolation.

- `BrokerMessage` — message DTO with body, properties, delivery count, sequence number, lock token
- `QueueEngine` — `System.Threading.Channels`-based FIFO with peek-lock semantics
- `LockManager` — tracks locked messages, auto-expires, re-enqueues on timeout
- `TopicEngine` — manages a per-subscription `QueueEngine`, clones messages on send
- `MessageBroker` — central dispatcher, resolves queue vs topic/subscription by entity path

### Phase 2 — AMQP Protocol Layer

AMQPNetLite `ContainerHost` wired up to the Core engine.

- `AmqpListenerHost` — opens the listener, registers SASL mechanisms and processors
- `LinkProcessor` — handles link attach, auto-creates entities, wires sink/source endpoints
- `BrokerMessageSink` — incoming path (client → broker)
- `BrokerMessageSource` — outgoing path (broker → client) with peek-lock settlement
- `AmqpConverter` — `BrokerMessage` ↔ `Amqp.Message` conversion

This phase was where all the protocol-compliance challenges surfaced (see [section 7](#7-technical-challenges-and-resolutions)).

### Phase 3 — Management API and CLI

REST API for inspection, CLI for developer-friendly commands.

- `ManagementApi` — ASP.NET Core Minimal API
- `Program.cs` (Host) — `System.CommandLine` subcommands: `start`, `send`, `list`, `peek`, `purge`, `reset`
- `ConfigLoader` — parses `localbus.config.json` for pre-defined entities

### Phase 4 — Polish and Distribution

- SQLite persistence (`SqliteMessageStore`) — optional, opt-in via `--persist`
- `dotnet tool` packaging (`IsPackable`, `PackAsTool`, `ToolCommandName`)
- Documentation (`README.md`, this document)
- Integration tests using the real AMQPNetLite client against the listener

## 7. Technical Challenges and Resolutions

This section documents every non-trivial protocol-level issue encountered during Phase 2, because they are not obvious from documentation and required reading both the `Azure.Messaging.ServiceBus` SDK source and the AMQPNetLite source.

### 7.1 SASL negotiation mismatch (MSSBCBS)

**Symptom:** `None of the server sasl-mechanisms ([PLAIN]) are supported by the client (MSSBCBS)`

**Root cause:** When `UseDevelopmentEmulator=true` is present in the connection string, `Azure.Messaging.ServiceBus` exclusively requests a proprietary `MSSBCBS` SASL mechanism. It does not fall back to ANONYMOUS, PLAIN, or anything else. The server must advertise MSSBCBS for the handshake to even begin.

**Resolution:** Implemented a custom server-side SASL profile (`MssbCbsSaslProfile`) that claims to support `MSSBCBS` and accepts all `SaslInit` commands unconditionally (returns `SaslOutcome { Code = Ok }`). This matches the Azure SDK expectation — security enforcement happens later, at the CBS token exchange, which we also accept unconditionally.

```csharp
public sealed class MssbCbsSaslProfile : SaslProfile
{
    public MssbCbsSaslProfile() : base(new Symbol("MSSBCBS")) { }
    protected override DescribedList OnCommand(DescribedList command) =>
        new SaslOutcome { Code = SaslCode.Ok };
    // ...
}

listener.SASL.EnableMechanism(new Symbol("MSSBCBS"), new MssbCbsSaslProfile());
```

### 7.2 CBS token exchange

**Symptom:** After SASL succeeds, the SDK opens a link to address `$cbs` and sends a SAS token for validation.

**Root cause:** CBS (Claims-Based Security) is an Azure-specific protocol layered on top of AMQP. The SDK expects a response to its CBS `put-token` request before it will open any other links.

**Resolution:** Registered a `CbsRequestProcessor` for the `$cbs` address that accepts every put-token request with a 200 OK response.

### 7.3 MaxMessageSize misinterpreted as -1

**Symptom:** `The message (id:28756230, size:159 bytes) is larger than is currently allowed (-1 bytes)`

**Root cause:** The SDK client sends `max-message-size = ulong.MaxValue` in its Attach frame, meaning "no limit from my side". AMQPNetLite's default behaviour in `LinkProcessor` was echoing this value back in the server's Attach response. The client then reads its own `ulong.MaxValue` value, internally casts it to a **signed** `long`, gets **-1**, and decides that any message larger than -1 bytes (i.e. all of them) is too large.

**Resolution:** Explicitly override `MaxMessageSize` in the Attach response:

```csharp
attachContext.Attach.MaxMessageSize = (ulong)long.MaxValue;
```

This is `0x7FFF_FFFF_FFFF_FFFF` — still huge (9.2 exabytes), still positive when cast to signed long, so no real message ever exceeds it.

### 7.4 Message body encoding (Data vs AmqpValue)

**Symptom:** `A message with a value type of System.Byte[] cannot be bound to a string` in the Functions trigger.

**Root cause:** When we called `new Message(byte[])`, AMQPNetLite encoded the body as `AmqpValue(byte[])`. The Azure Functions Service Bus binding can convert an AMQP **`Data`** section (binary) to `string`, but refuses to convert an `AmqpValue(byte[])` to anything but `byte[]`.

**Resolution:** Construct the message with an explicit `BodySection`:

```csharp
var message = new Message
{
    BodySection = new Data { Binary = brokerMessage.Body.ToArray() },
    // ...
};
```

This is how the real Service Bus encodes string-bodied messages, and it maps cleanly to all the standard Functions parameter types (`string`, `byte[]`, `ServiceBusReceivedMessage`, `MyPoco` via JSON).

### 7.5 Lock token delivery (the big one)

**Symptom:** `This operation is not supported for peeked messages. Only messages received in PeekLock mode can be settled.`

This error appeared despite the emulator being in PeekLock mode and sending what looked like a valid lock token in `MessageAnnotations["x-opt-lock-token"]`.

**Root cause:** Reading the Azure SDK source (`sdk/servicebus/Azure.Messaging.ServiceBus/src/Amqp/AmqpMessageConverter.cs`), the `AmqpMessageToSBReceivedMessage` method does this:

```csharp
if (GuidUtilities.TryParseGuidBytes(amqpMessage.DeliveryTag, out Guid lockToken))
{
    sbMessage.LockTokenGuid = lockToken;
}
```

The lock token is read **only** from the AMQP delivery tag, and `TryParseGuidBytes` requires exactly **16 bytes** (the size of a `Guid`). AMQPNetLite's default behaviour in `ListenerLink.SendMessageInternal`:

```csharp
if (delivery.Tag == null)
    delivery.Tag = Delivery.GetDeliveryTag(tag);  // 4-byte sequential uint
```

A 4-byte tag cannot be parsed as a 16-byte Guid, so `LockTokenGuid` stays `Guid.Empty`, and the SDK rejects every settlement attempt with "peeked message" — before even sending a frame.

**Resolution:** Register an `IHandler` on the `ConnectionListener.HandlerFactory` that intercepts `EventId.SendDelivery`. The event fires *before* the 4-byte fallback runs, and provides access to the `Delivery` object:

```csharp
// In ListenerLink.SendMessageInternal:
handler.Handle(Event.Create(EventId.SendDelivery, ..., context: delivery));
if (delivery.Tag == null)
    delivery.Tag = Delivery.GetDeliveryTag(tag);
```

The `DeliveryTagHandler` reads the string lock token we already stored in `DeliveryAnnotations["x-opt-lock-token"]`, converts it back to a `Guid`, and writes the 16-byte representation to `delivery.Tag`:

```csharp
public void Handle(Event evnt)
{
    var context = evnt.Context;
    var message = _messageProperty.GetValue(context) as Message;
    var lockTokenStr = (string)message.DeliveryAnnotations[LockTokenKey];
    var lockToken = Guid.Parse(lockTokenStr);
    _tagProperty.SetValue(context, lockToken.ToByteArray());  // 16 bytes
}
```

**Reflection trade-off:** `Amqp.Delivery` is an internal class in AMQPNetLite, so `delivery.Tag` and `delivery.Message` are accessed via cached reflection. To detect library upgrades that break this, the `DeliveryTagHandler` constructor **fails fast** if the reflected properties are null — better a loud startup error than silent message-delivery failures.

### 7.6 Lock renewal via `$management`

**Symptom:** `Message processing error (Action=RenewLock, EntityPath=sample-queue, Endpoint=localhost) ... This operation is not supported for peeked messages`

**Root cause:** The Azure Functions Service Bus extension periodically renews the lock on every message it is currently processing. For the v5 extension / `Azure.Messaging.ServiceBus` combo, this renewal is **not** a regular AMQP Disposition frame — it is an RPC-style request sent to a special `$management` address, with an operation name of `com.microsoft:renew-lock-for-message` and the lock token in the request body.

**Resolution:** Implemented `ManagementRequestProcessor` registered on the `$management` address. It parses the operation name, extracts the lock tokens from the request body, and calls `MessageBroker.RenewLock(Guid)` for each one, returning the new expiry times. The SDK may address this as `{entity-name}/$management` (e.g. `sample-queue/$management`), so an `AddressResolver` normalises any `*/$management` address to a single `$management` processor:

```csharp
_host.AddressResolver = (_, attach) =>
{
    var address = attach.Role
        ? (attach.Source as Source)?.Address
        : (attach.Target as Target)?.Address;

    if (address != null && address.EndsWith("/$management", StringComparison.OrdinalIgnoreCase))
        return "$management";

    return null;
};
```

### 7.7 Supporting the legacy SDK (ANONYMOUS SASL)

**Symptom:** `None of the server sasl-mechanisms ([MSSBCBS]) are supported by the client (ANONYMOUS)` when using `Microsoft.Azure.ServiceBus` v4 (old SDK).

**Root cause:** The old SDK does not know about `MSSBCBS`. It uses ANONYMOUS SASL and relies on CBS for authorization.

**Resolution:** Advertise both mechanisms:

```csharp
listener.SASL.EnableMechanism(new Symbol("MSSBCBS"), new MssbCbsSaslProfile());
listener.SASL.EnableAnonymousMechanism = true;
```

The new SDK requests MSSBCBS and ignores ANONYMOUS; the old SDK picks ANONYMOUS. Both result in CBS token exchange (which we accept unconditionally), so both reach the same successful state.

### 7.8 `ExtractLockToken` value-type mismatch

After changing the serialized lock token from `Guid` to `string` (to avoid UUID byte-order incompatibilities between AMQPNetLite and `Microsoft.Azure.Amqp`), the internal `ExtractLockToken` method still checked `tokenObj is Guid` and always returned `Guid.Empty`.

**Resolution:** Accept both shapes, with fallback to `DeliveryAnnotations`:

```csharp
if (tokenObj is Guid g) return g;
if (tokenObj is string s && Guid.TryParse(s, out var parsed)) return parsed;
```

## 8. Trade-offs and Known Limitations

### Intentional trade-offs

| Decision | Trade-off |
|---|---|
| In-memory by default | State is lost on restart — intentional for clean dev slate; SQLite available via `--persist` |
| No TLS | Simplifies setup, localhost only — not suitable beyond dev |
| No session support | Vast majority of Functions don't use sessions; can be added in v1.1 if demand emerges |
| No SQL filters on subscriptions | Each subscription receives all topic messages; most Functions filter in code anyway |
| Reflection on AMQPNetLite internals | Fragile to version upgrades; mitigated by fail-fast assertion |
| `dotnet tool` rather than Windows service | No admin rights needed, matches Azurite developer experience |

### Known limitations

- **No message scheduling** — `ScheduledEnqueueTime` is ignored (message is delivered immediately)
- **No auto-forwarding** — `ForwardTo` property is ignored
- **No message deferral** — deferred messages behave like locked messages
- **No partitioned entities** — all queues/topics are single-partition
- **No duplicate detection** — messages with the same `MessageId` are not deduplicated

All of the above can be added in future versions without breaking protocol compatibility.

## 9. Testing Strategy

### Unit tests (`LocalServiceBus.Core.Tests`)

30 tests covering the pure engine:

- FIFO ordering under concurrent producers
- Peek-lock acquire/complete/abandon/renew
- Lock expiry and automatic re-enqueue
- Max delivery count → auto dead-letter
- Topic fan-out to multiple subscriptions
- Dead-letter queue inspection and resubmit

These tests have zero AMQP dependencies. They hammer the `MessageBroker`, `QueueEngine`, `TopicEngine`, and `LockManager` directly.

### Integration tests (`LocalServiceBus.Integration.Tests`)

4 tests that start a real `AmqpListenerHost` on an ephemeral port and connect to it with the AMQPNetLite **client** using `SaslProfile.Anonymous`:

1. Send and receive on a queue via AMQP
2. Topic fan-out to two subscriptions via AMQP
3. Reject → message moves to DLQ
4. Auto-create queue on first link attach

These tests validate the entire protocol pipeline end-to-end. They were invaluable during development for catching the MaxMessageSize, delivery tag, and message body issues.

### Manual end-to-end validation

A demo solution (`LocalBusDemo`) was built as a smoke test:

- **ASP.NET Core Web API** with a browser-based dashboard that sends messages to the emulator
- **Azure Functions isolated worker (.NET 8)** with `[ServiceBusTrigger]` attribute that logs messages and appends them to a file
- A shared file (`received-messages.jsonl`) that the API displays live

This exercised every code path that matters: SASL, CBS, auto-create, send, receive, lock-token round-trip, body binding, lock renewal, and settlement.

## 10. References

The design decisions in this document were informed by a combination of official documentation, SDK source code, and AMQPNetLite source code. Every reference was used at least once during implementation.

### Official Microsoft documentation

- [AMQP 1.0 in Azure Service Bus and Event Hubs (protocol guide)](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-protocol-guide) — referenced for message annotation naming, CBS flow, `$management` operations
- [AMQP 1.0 request/response operations in Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-request-response) — referenced for `com.microsoft:renew-lock-for-message` operation wire format
- [Azure Service Bus Emulator installer](https://github.com/Azure/azure-service-bus-emulator-installer) — referenced for connection-string compatibility (the `UseDevelopmentEmulator=true` flag)
- [Azure Functions — ServiceBusTrigger binding](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-service-bus-trigger) — referenced for the expected parameter types and settlement behaviour

### SDK source code (read during debugging)

- [`Azure.Messaging.ServiceBus` on GitHub](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/servicebus/Azure.Messaging.ServiceBus) — specifically:
  - `src/Amqp/AmqpMessageConverter.cs` — confirmed the lock token is read only from `amqpMessage.DeliveryTag` (section 7.5)
  - `src/Amqp/AmqpReceiver.cs` — confirmed the settlement, disposition, and renew-lock wire formats (sections 7.5, 7.6)
- [Fix Guid handling PR #33881](https://github.com/Azure/azure-sdk-for-net/pull/33881) — context on the `GuidUtilities` byte ordering used for the delivery tag

### AMQPNetLite source code

- [`AMQPNetLite` on GitHub](https://github.com/Azure/amqpnetlite) — specifically:
  - `src/Listener/ListenerLink.cs` — found the `handler.Handle(EventId.SendDelivery, ...)` extension point before the 4-byte fallback (section 7.5)
  - `src/Listener/ContainerHost.cs` — documented the `AddressResolver`, `RegisterRequestProcessor`, `RegisterLinkProcessor` APIs
  - `src/Listener/ConnectionListener.cs` — documented the `HandlerFactory` pattern
  - `src/Delivery.cs` — confirmed `Tag` and `Message` are the public properties exposed on the internal class (targets for reflection)
  - `src/Sasl/SaslProfile.cs` — used as the base class for `MssbCbsSaslProfile`

### Protocol specifications

- [OASIS AMQP 1.0 spec (ISO/IEC 19494:2014)](https://docs.oasis-open.org/amqp/core/v1.0/amqp-core-complete-v1.0.pdf) — referenced for frame structure, SASL negotiation, protocol ID 3 vs 0, message format

### Related community projects (for comparison / inspiration)

- [mxsdev/LocalSandbox](https://github.com/mxsdev/LocalSandbox) — similar concept in TypeScript; reviewed its approach to CBS token handling
- [GitHub issue: delivery_tag vs x-opt-lock-token](https://github.com/Azure/azure-sdk-for-python/issues/4598) — confirmed that the delivery tag takes precedence for active messages across SDK implementations

### Internal development artefacts

- `plans/local_service_bus_emulator_eaa0daf9.plan.md` — the original four-phase plan
- `plans/docker_remote_context_setup_7ca1d73d.plan.md` — earlier attempt at remote Docker context; abandoned in favour of this emulator

---

## Appendix A — Why not just use `amqp.net`?

AMQPNetLite was chosen over its sibling `amqp.net` (Microsoft.Azure.Amqp) because:

1. **Listener support** — AMQPNetLite provides a full `ContainerHost` implementation for the listener side; `Microsoft.Azure.Amqp` is client-only in its public surface
2. **Smaller surface** — AMQPNetLite is a single self-contained library with a stable API; `Microsoft.Azure.Amqp` has many internal types and is intentionally non-public
3. **Cross-platform** — Both are managed, but AMQPNetLite has cleaner netstandard / .NET 8 targeting
4. **License** — Apache 2.0 is permissive and compatible with our internal distribution

## Appendix B — Why build vs buy (revisited)

Every 3-6 months this decision should be re-evaluated:

- If Microsoft releases a **Docker-free** version of the official Service Bus Emulator → migrate to it
- If .NET Aspire adds a native (non-container) Service Bus resource → evaluate it
- If our Docker licensing blocker is resolved → consider the official emulator

For now (April 2026), the custom emulator is the only option that meets all goals. Total implementation effort was approximately **2 weeks** of focused work (design, Phase 1-4, debugging). Ongoing maintenance is expected to be minimal — the protocol surface is stable and the internal API of AMQPNetLite has not changed in years.
