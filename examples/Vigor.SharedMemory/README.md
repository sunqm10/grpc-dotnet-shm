# Health Checks - Shared Memory Transport Example

This example demonstrates gRPC health checks over the shared memory transport.

## Features

- Implements the standard gRPC health checking protocol
- Watch health status changes in real-time over shared memory
- Demonstrates server-side streaming over shared memory

## Running the Example

### Start the Server

```bash
cd Server
dotnet run
```

### Run the Client (in a separate terminal)

```bash
cd Client
dotnet run
```

## What it Demonstrates

1. **Health Check Protocol**: Standard gRPC health check service implementation
2. **Watch Streaming**: Server streaming to watch health status changes
3. **Shared Memory Streaming**: Long-running server stream over shared memory

## Shared Memory Transport

This example uses the shared memory transport instead of TCP for ultra-low latency
same-machine communication. The transport is Windows-only for now.

See the main [Grpc.Net.SharedMemory](../../../src/Grpc.Net.SharedMemory/README.md) documentation for more details.
