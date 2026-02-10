# Echo Shared Memory Example

This example demonstrates unary and bidirectional streaming gRPC calls over shared memory transport using the idiomatic `UseSharedMemory()` / `ShmHttpHandler` integration.

## Features

- **Unary Echo**: Send a single message and receive it echoed back
- **Bidirectional Streaming Echo**: Send a stream of messages and receive them echoed back

## Prerequisites

- .NET 9.0 SDK or later

## Running

### Start the server

```bash
cd Server
dotnet run
```

### Start the client (in a separate terminal)

```bash
cd Client
dotnet run
```

## Expected output

**Client:**
```
=== Unary Echo ===
Response: Hello, shared memory!

=== Bidirectional Streaming Echo ===
  Sending: First
  Received: First
  Sending: Second
  Received: Second
  Sending: Third
  Received: Third

Echo example completed successfully!
```
