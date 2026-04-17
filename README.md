# LocalServiceBus

A standalone .NET 8 console application that emulates Azure Service Bus locally via the **AMQP 1.0 protocol**. The real `Azure.Messaging.ServiceBus` SDK connects to it transparently — all Azure Functions using `[ServiceBusTrigger]` work with **zero code changes**.

---

## Table of Contents

- [How It Works](#how-it-works)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Developer Setup (Azure Functions)](#developer-setup-azure-functions)
- [Developer Setup (Application Services)](#developer-setup-application-services)
- [CLI Reference](#cli-reference)
- [REST Management API](#rest-management-api)
- [Configuration File](#configuration-file)
- [Entity Behaviour](#entity-behaviour)
- [Supported Features](#supported-features)
- [Architecture](#architecture)
- [Project Structure](#project-structure)
- [Building from Source](#building-from-source)
- [Packaging as a Global Tool](#packaging-as-a-global-tool)
- [Troubleshooting](#troubleshooting)

---

## How It Works

The emulator starts an **AMQP 1.0 listener** on port `5672` (the same port Azure Service Bus uses). When the `Azure.Messaging.ServiceBus` SDK connects, it speaks the same protocol it would use against the real Azure endpoint — no mocking, no stubs.

- **Senders** open an AMQP sender link to a queue or topic address and transfer messages.
- **Receivers / Functions** open an AMQP receiver link, and the emulator delivers messages with peek-lock semantics.
- **Disposition** (Accept, Reject, Release) is handled identically to Azure — accepted messages are completed, rejected messages go to the dead-letter queue, released messages are abandoned and re-queued.
- **Topics** fan out a copy of each message to every registered subscription.
- **Auto-create**: if an application opens a link to a queue or subscription that doesn't exist yet, the emulator creates it automatically — no upfront registration required.

The **REST Management API** on port `9090` gives developers and QA a way to inject test messages, inspect queue depths, peek dead-letter messages, and reset state — all without writing any code.

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 8.0 or later | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Azure Functions Core Tools | v4 | Only needed if running Functions locally |

No Docker. No Azure subscription. No additional services.

---

## Installation

### Option A — Install from the team's NuGet feed

Once the package has been published to your internal feed:

```powershell
dotnet tool install --global LocalServiceBus --add-source https://your-nuget-feed/v3/index.json
```

### Option B — Install from local build (from source)

```powershell
git clone https://github.com/kg349/LocalServiceBus.git
cd LocalServiceBus
dotnet pack src/LocalServiceBus.Host/LocalServiceBus.Host.csproj -o ./nupkg
dotnet tool install --global LocalServiceBus --add-source ./nupkg
```

### Verify the installation

```powershell
localbus --help
```

Expected output:

```
Description:
  LocalServiceBus — Azure Service Bus emulator for local development

Usage:
  localbus [command] [options]

Commands:
  start    Start the LocalServiceBus emulator
  send     Send a message to a queue or topic
  list     List queues or topics
  peek     Peek messages in a queue
  purge    Purge all messages from a queue
  reset    Clear all emulator state
```

### Updating to a newer version

```powershell
dotnet tool update --global LocalServiceBus --add-source ./nupkg
```

### Uninstalling

```powershell
dotnet tool uninstall --global LocalServiceBus
```

---

## Developer Setup (Azure Functions)

This is the most common scenario — triggering Azure Functions locally with `[ServiceBusTrigger]`.

### Step 1 — Start the emulator

Open a terminal and run:

```powershell
localbus start
```

You will see:

```
LocalServiceBus Emulator started
  AMQP:  amqp://localhost:5672
  REST:  http://localhost:9090
  Connection string: Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;Port=5672

Press Ctrl+C to stop.
```

Leave this terminal open. The emulator runs until you press `Ctrl+C`.

### Step 2 — Update local.settings.json

In your Azure Functions project, open `local.settings.json` and change the Service Bus connection string to point at the emulator:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection": "Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;UseDevelopmentEmulator=true"
  }
}
```

> **Important:** Do not commit `local.settings.json` to source control. It is already excluded by the default Functions `.gitignore`.

If your Functions use a **named connection** (e.g. `ServiceBusConnection__fullyQualifiedNamespace`), use the full connection string format shown above instead.

### Step 3 — Start your Functions

```powershell
func start
```

Your Functions will connect to the emulator. All `[ServiceBusTrigger]` attributes work without modification.

### Step 4 — Inject a test message

```powershell
# Send a message to a queue
localbus send queue orders-queue '{"orderId":"abc-123","amount":99.99}'

# Or via the REST API
curl -X POST http://localhost:9090/queues/orders-queue/messages `
     -H "Content-Type: application/json" `
     -d '{"orderId":"abc-123","amount":99.99}'
```

Your Function will fire immediately.

### Step 5 — Inspect state

```powershell
# See all queues and message counts
localbus list queues

# Check dead-letter queue
localbus peek orders-queue --deadletter

# Move dead-letters back to the main queue to retry
curl -X POST http://localhost:9090/queues/orders-queue/deadletter/resubmit
```

---

## Developer Setup (Application Services)

For services that **send** messages (not Functions), update the connection string in your `appsettings.Development.json`:

```json
{
  "ServiceBus": {
    "ConnectionString": "Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;UseDevelopmentEmulator=true",
    "QueueName": "orders-queue"
  }
}
```

The `ServiceBusClient` and `ServiceBusSender` classes work exactly as normal:

```csharp
var client = new ServiceBusClient(connectionString);
var sender = client.CreateSender("orders-queue");
await sender.SendMessageAsync(new ServiceBusMessage(body));
```

No code changes required.

---

## CLI Reference

### `localbus start`

Starts the AMQP emulator and REST management API.

```powershell
localbus start [options]

Options:
  --port <port>      AMQP listener port [default: 5672]
  --api <port>       REST management API port [default: 9090]
  --config <path>    Path to localbus.config.json for pre-defined entities
```

Examples:

```powershell
localbus start
localbus start --port 5672 --api 9090
localbus start --config ./localbus.config.json
```

---

### `localbus send`

Sends a message to a queue or topic via the REST API (emulator must be running).

```powershell
localbus send <entityType> <entityName> <body> [options]

Arguments:
  entityType    'queue' or 'topic'
  entityName    Name of the queue or topic
  body          Message body as a JSON string

Options:
  --api-url <url>    REST API base URL [default: http://localhost:9090]
```

Examples:

```powershell
localbus send queue orders-queue '{"orderId":"123"}'
localbus send topic order-events '{"event":"OrderPlaced","orderId":"123"}'
```

---

### `localbus list`

Lists all queues or topics with message counts.

```powershell
localbus list <entityType> [options]

Arguments:
  entityType    'queues' or 'topics'
```

Examples:

```powershell
localbus list queues
localbus list topics
```

---

### `localbus peek`

Peeks at messages in a queue without consuming them.

```powershell
localbus peek <queueName> [options]

Options:
  --deadletter      Peek dead-letter messages instead of active messages
  --api-url <url>   REST API base URL [default: http://localhost:9090]
```

Examples:

```powershell
localbus peek orders-queue
localbus peek orders-queue --deadletter
```

---

### `localbus purge`

Removes all active messages from a queue.

```powershell
localbus purge <queueName> [options]
```

Example:

```powershell
localbus purge orders-queue
```

---

### `localbus reset`

Clears **all** state — all queues, topics, subscriptions, and messages.

```powershell
localbus reset [options]

Options:
  --api-url <url>   REST API base URL [default: http://localhost:9090]
```

---

## REST Management API

The REST API runs on port `9090` by default and is available while the emulator is running. It is useful for CI pipelines, Postman collections, and custom test setup/teardown scripts.

### Queues

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/queues` | List all queues with active and dead-letter counts |
| `GET` | `/queues/{name}` | Detail for a single queue |
| `POST` | `/queues/{name}/messages` | Inject a message (raw JSON body) |
| `DELETE` | `/queues/{name}/messages` | Purge all active messages |
| `GET` | `/queues/{name}/deadletter` | List dead-letter messages (read-only) |
| `POST` | `/queues/{name}/deadletter/resubmit` | Move all DLQ messages back to the main queue |

### Topics

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/topics` | List all topics |
| `GET` | `/topics/{name}/subscriptions` | List subscriptions with message counts |
| `POST` | `/topics/{name}/messages` | Publish a message (fans out to all subscriptions) |

### General

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Returns `200 OK` when the emulator is running |
| `POST` | `/reset` | Clears all queues, topics, subscriptions, and messages |

### Example — Inject a message with curl

```powershell
curl -X POST http://localhost:9090/queues/orders-queue/messages `
     -H "Content-Type: application/json" `
     -d '{"orderId":"test-001","customerId":"c-42","amount":149.99}'
```

### Example — Check queue depth

```powershell
curl http://localhost:9090/queues/orders-queue
```

Response:

```json
{
  "name": "orders-queue",
  "activeMessages": 3,
  "deadLetterMessages": 1,
  "createdAt": "2026-04-16T09:00:00Z"
}
```

### Example — Resubmit dead-letter messages

```powershell
curl -X POST http://localhost:9090/queues/orders-queue/deadletter/resubmit
```

---

## Configuration File

By default the emulator auto-creates any queue, topic, or subscription the first time the SDK accesses it. If you want entities **pre-created on startup** (useful so Functions don't error on the first connection attempt), use a config file.

Create `localbus.config.json` alongside your solution or in a shared location:

```json
{
  "queues": [
    { "name": "orders-queue",   "maxDeliveryCount": 5 },
    { "name": "payments-queue", "maxDeliveryCount": 3 },
    { "name": "email-queue",    "maxDeliveryCount": 10 }
  ],
  "topics": [
    {
      "name": "order-events",
      "subscriptions": [
        { "name": "notification-handler" },
        { "name": "audit-handler" },
        { "name": "reporting-handler" }
      ]
    },
    {
      "name": "payment-events",
      "subscriptions": [
        { "name": "ledger-handler" }
      ]
    }
  ]
}
```

Start with the config:

```powershell
localbus start --config ./localbus.config.json
```

### Configuration properties

| Property | Type | Default | Description |
|---|---|---|---|
| `queues[].name` | string | required | Queue name (must match `[ServiceBusTrigger]` attribute) |
| `queues[].maxDeliveryCount` | int | `10` | Number of delivery attempts before the message is dead-lettered |
| `topics[].name` | string | required | Topic name |
| `topics[].subscriptions[].name` | string | required | Subscription name |
| `topics[].subscriptions[].maxDeliveryCount` | int | `10` | Max delivery attempts per subscription |

---

## Entity Behaviour

### Queues

- **FIFO ordering** — messages are delivered in the order they were sent
- **Peek-lock** — a message is locked when received; other consumers cannot receive it until the lock expires, it is abandoned, or it is completed
- **Lock duration** — default 30 seconds; the lock expires automatically if not settled in time
- **Max delivery count** — if a message is abandoned (or its lock expires) more times than `maxDeliveryCount`, it is moved to the dead-letter queue automatically
- **Dead-letter queue** — available at `{queueName}/$deadletterqueue`; messages include the dead-letter reason

### Topics and Subscriptions

- **Fan-out** — each published message is cloned and delivered independently to every subscription
- **Independent delivery** — completing a message in subscription A does not affect subscription B
- **Per-subscription DLQ** — each subscription has its own dead-letter queue

### Locks

- A received message is locked with a unique lock token
- The lock token must be used to complete, dead-letter, or abandon the message
- If the lock expires without settlement, the message is automatically abandoned and re-queued (delivery count increments)

---

## Supported Features

| Feature | Supported |
|---|---|
| Queues | Yes |
| Topics + Subscriptions | Yes |
| FIFO ordering | Yes |
| Peek-lock | Yes |
| Complete | Yes |
| Abandon | Yes |
| Dead-letter | Yes |
| Lock expiry + auto-abandon | Yes |
| Max delivery count | Yes |
| Dead-letter queue peek | Yes |
| DLQ resubmit | Yes |
| Auto-create entities on first access | Yes |
| Pre-defined entities via config | Yes |
| Multiple concurrent consumers | Yes |
| `Azure.Messaging.ServiceBus` SDK | Yes |
| `[ServiceBusTrigger]` Azure Functions | Yes |
| Message properties (ContentType, CorrelationId, Subject, etc.) | Yes |
| Application properties | Yes |
| Session-based messaging | No |
| Message scheduling | No |
| Message forwarding | No |
| Filters / SQL rules on subscriptions | No |
| TLS / AMQPS | No (localhost only) |

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│               LocalServiceBus Emulator               │
│                                                     │
│  ┌──────────────────┐      ┌───────────────────┐   │
│  │  AMQP 1.0 Layer  │      │  REST API          │   │
│  │  port 5672       │      │  port 9090         │   │
│  │                  │      │                   │   │
│  │  LinkProcessor   │      │  ManagementApi    │   │
│  │  ├ BrokerSink    │      │  (Minimal API)    │   │
│  │  └ BrokerSource  │      └────────┬──────────┘   │
│  └────────┬─────────┘               │              │
│           │                         │              │
│           └────────────┬────────────┘              │
│                        ▼                           │
│           ┌────────────────────────┐               │
│           │     MessageBroker      │               │
│           │                        │               │
│           │  ┌──────────────────┐  │               │
│           │  │   QueueEngine    │  │               │
│           │  │  Channel<T> FIFO │  │               │
│           │  │  LockManager     │  │               │
│           │  │  DeadLetterQueue │  │               │
│           │  └──────────────────┘  │               │
│           │                        │               │
│           │  ┌──────────────────┐  │               │
│           │  │   TopicEngine    │  │               │
│           │  │  Fan-out         │  │               │
│           │  │  └ QueueEngine   │  │               │
│           │  │    per sub       │  │               │
│           │  └──────────────────┘  │               │
│           └────────────────────────┘               │
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

### Key design decisions

- **No Docker** — runs as a native .NET process, no container runtime required
- **In-memory by default** — state is lost when the emulator stops; use `--persist` flag for SQLite persistence across restarts
- **Auto-create** — the first SDK link open creates the entity; no upfront registration
- **Real AMQP protocol** — not a mock or stub; the SDK's AMQP frames flow through unchanged

---

## Project Structure

```
LocalServiceBus/
├── src/
│   ├── LocalServiceBus.Host/              # dotnet tool entry point + CLI commands
│   │   ├── Program.cs                     # System.CommandLine root/subcommands
│   │   ├── ConfigLoader.cs                # Parses localbus.config.json
│   │   └── appsettings.json
│   ├── LocalServiceBus.Core/              # Pure engine — no protocol dependencies
│   │   ├── Engine/
│   │   │   ├── MessageBroker.cs           # Central dispatcher
│   │   │   ├── QueueEngine.cs             # FIFO, peek-lock, DLQ
│   │   │   ├── TopicEngine.cs             # Fan-out to subscription queues
│   │   │   └── LockManager.cs             # Lock tracking + expiry timer
│   │   ├── Models/
│   │   │   ├── BrokerMessage.cs
│   │   │   ├── QueueEntity.cs
│   │   │   ├── TopicEntity.cs
│   │   │   └── SubscriptionEntity.cs
│   │   └── Store/
│   │       ├── IMessageStore.cs
│   │       ├── InMemoryMessageStore.cs
│   │       └── SqliteMessageStore.cs      # Optional persistence
│   ├── LocalServiceBus.Amqp/              # AMQP 1.0 protocol layer
│   │   ├── AmqpListenerHost.cs            # ContainerHost setup
│   │   └── Processors/
│   │       ├── LinkProcessor.cs           # Handles link attach + auto-create
│   │       ├── BrokerMessageSink.cs       # Incoming messages (send path)
│   │       ├── BrokerMessageSource.cs     # Outgoing messages (receive path)
│   │       └── AmqpConverter.cs           # AMQP ↔ BrokerMessage conversion
│   └── LocalServiceBus.Management/        # ASP.NET Core Minimal API
│       └── ManagementApi.cs
├── tests/
│   ├── LocalServiceBus.Core.Tests/        # 30 unit tests
│   └── LocalServiceBus.Integration.Tests/ # 4 AMQP integration tests
├── localbus.config.json                   # Sample configuration
└── README.md
```

---

## Building from Source

```powershell
# Clone
git clone https://github.com/kg349/LocalServiceBus.git
cd LocalServiceBus

# Build
dotnet build

# Run all tests (30 unit + 4 integration)
dotnet test

# Run only unit tests
dotnet test tests/LocalServiceBus.Core.Tests

# Run only integration tests
dotnet test tests/LocalServiceBus.Integration.Tests
```

---

## Packaging as a Global Tool

```powershell
# Build the NuGet package
dotnet pack src/LocalServiceBus.Host/LocalServiceBus.Host.csproj -o ./nupkg

# Install globally from local package
dotnet tool install --global LocalServiceBus --add-source ./nupkg

# Verify
localbus --help
```

To publish to an internal NuGet feed (e.g. Azure Artifacts):

```powershell
dotnet nuget push ./nupkg/LocalServiceBus.1.0.0.nupkg --source https://your-feed/v3/index.json --api-key YOUR_KEY
```

---

## Troubleshooting

### Port already in use

```
System.Net.Sockets.SocketException: Only one usage of each socket address is permitted
```

Another process is using port 5672 or 9090. Use custom ports:

```powershell
localbus start --port 5673 --api 9091
```

Update your connection string to match:

```
Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;Port=5673
```

---

### Functions connect but triggers never fire

This usually means the queue or subscription name in `[ServiceBusTrigger]` does not match what the emulator created. Check:

```powershell
localbus list queues
localbus list topics
```

If the entity is missing, either send a message to auto-create it first, or add it to `localbus.config.json` and restart with `--config`.

---

### Messages go straight to dead-letter

The Function is throwing an exception on every attempt and exceeding `maxDeliveryCount`. Check:

```powershell
localbus peek orders-queue --deadletter
```

The response includes the `deadLetterReason` and `deadLetterErrorDescription` fields showing why it was dead-lettered. Fix the Function error, resubmit:

```powershell
curl -X POST http://localhost:9090/queues/orders-queue/deadletter/resubmit
```

---

### `localbus` command not found after install

The dotnet tools directory is not on your PATH. Add it:

```powershell
# Add to your PowerShell profile
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"

# Or permanently via System Environment Variables:
# Path → Add: %USERPROFILE%\.dotnet\tools
```

---

### Emulator state lost between restarts

By default the emulator is in-memory only. All messages are lost when you stop it. This is intentional for a clean slate on each dev session.

If you need messages to survive a restart (e.g. for a long-running debugging session), SQLite persistence is built in — add `--persist` to the start command *(requires uncommenting the flag in `Program.cs` for the current build — coming in v1.1)*.

---

### Verifying the emulator is running

```powershell
curl http://localhost:9090/health
```

Response: `{"status":"healthy","timestamp":"2026-04-16T09:00:00Z"}`
