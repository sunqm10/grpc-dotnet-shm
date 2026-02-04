# gRPC Reflection - Shared Memory Transport Example

This example demonstrates gRPC server reflection over the shared memory transport.

## Features

- Lists all available gRPC services using server reflection
- Demonstrates bidirectional streaming over shared memory
- Shows how to discover services at runtime

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

1. **Server Reflection**: Query available gRPC services at runtime
2. **Bidirectional Streaming**: The reflection API uses bidirectional streaming
3. **Service Discovery**: Dynamically discover services without proto files

## Shared Memory Transport

This example uses the shared memory transport instead of TCP for ultra-low latency
same-machine communication.

The transport is Windows-only for now.

See the main [Grpc.Net.SharedMemory](../../../src/Grpc.Net.SharedMemory/README.md) documentation for more details.
