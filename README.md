# LocalServiceBus

A standalone .NET console application that emulates Azure Service Bus locally via AMQP 1.0. The real `Azure.Messaging.ServiceBus` SDK connects to it transparently — all your Functions with `[ServiceBusTrigger]` work with zero code changes.

## Quick Start

```bash
# Install as global tool
dotnet tool install --global LocalServiceBus --add-source ./nupkg

# Start the emulator
localbus start

# In another terminal, start your Functions
func start
```

## Connection String

Point your `local.settings.json` at the emulator:

```json
{
  "Values": {
    "ServiceBusConnection": "Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;UseDevelopmentEmulator=true"
  }
}
```

## CLI Commands

```
localbus start                          # Start emulator (default ports)
localbus start --port 5672 --api 9090   # Custom ports
localbus start --config localbus.config.json  # Pre-define entities

localbus send queue orders-queue '{"orderId":"123"}'
localbus send topic order-events '{"event":"OrderPlaced"}'
localbus list queues
localbus list topics
localbus peek orders-queue
localbus peek orders-queue --deadletter
localbus purge orders-queue
localbus reset
```

## REST Management API (port 9090)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/queues` | List all queues with message counts |
| GET | `/queues/{name}` | Queue detail (active + DLQ counts) |
| POST | `/queues/{name}/messages` | Inject a test message (JSON body) |
| DELETE | `/queues/{name}/messages` | Purge all messages |
| GET | `/queues/{name}/deadletter` | Peek dead-letter messages |
| POST | `/queues/{name}/deadletter/resubmit` | Move DLQ messages back to queue |
| GET | `/topics` | List all topics |
| GET | `/topics/{name}/subscriptions` | List subscriptions |
| POST | `/topics/{name}/messages` | Publish to topic (fans out) |
| GET | `/health` | Emulator health check |
| POST | `/reset` | Clear all state |

## Configuration File

Optionally pre-create entities with `localbus.config.json`:

```json
{
  "queues": [
    { "name": "orders-queue", "maxDeliveryCount": 5 },
    { "name": "payments-queue", "maxDeliveryCount": 3 }
  ],
  "topics": [
    {
      "name": "order-events",
      "subscriptions": [
        { "name": "notification-handler" },
        { "name": "audit-handler" }
      ]
    }
  ]
}
```

Without a config file, entities are auto-created the first time the SDK tries to access them.

## Architecture

```
┌──────────────────────────────────────┐
│        LocalServiceBus Emulator      │
│                                      │
│  ┌─────────────┐  ┌──────────────┐   │
│  │ AMQP 1.0    │  │ REST API     │   │
│  │ port 5672   │  │ port 9090    │   │
│  └──────┬──────┘  └──────┬───────┘   │
│         │                │           │
│         v                v           │
│  ┌──────────────────────────────┐    │
│  │      Message Broker          │    │
│  │  ┌─────────┐ ┌───────────┐  │    │
│  │  │ Queues  │ │  Topics   │  │    │
│  │  │ (FIFO)  │ │ (fan-out) │  │    │
│  │  └─────────┘ └───────────┘  │    │
│  └──────────────────────────────┘    │
└──────────────────────────────────────┘
         ▲                ▲
         │                │
   Azure Functions    CLI / Browser
   [ServiceBusTrigger]
```

## Project Structure

```
LocalServiceBus/
├── src/
│   ├── LocalServiceBus.Host/          # Console app / dotnet tool entry point
│   ├── LocalServiceBus.Core/          # Engine, store, models
│   ├── LocalServiceBus.Amqp/          # AMQP 1.0 protocol layer
│   └── LocalServiceBus.Management/    # REST management API
├── tests/
│   ├── LocalServiceBus.Core.Tests/    # Unit tests
│   └── LocalServiceBus.Integration.Tests/  # AMQP integration tests
└── localbus.config.json               # Sample config
```

## Building from Source

```bash
dotnet build
dotnet test
```

## Packaging as a Global Tool

```bash
dotnet pack src/LocalServiceBus.Host/LocalServiceBus.Host.csproj -o ./nupkg
dotnet tool install --global LocalServiceBus --add-source ./nupkg
```
