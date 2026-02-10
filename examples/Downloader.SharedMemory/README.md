# File Download - Shared Memory Transport Example

This example demonstrates server-side streaming for large file downloads over the shared memory transport using the idiomatic `UseSharedMemory()` / `ShmHttpHandler` integration.

## Features

- Server-side streaming for downloading large files using standard gRPC service and stubs
- Demonstrates chunked data transfer over shared memory
- Shows metadata + data streaming pattern
- Uses `UseSharedMemory()` on the server and `ShmHttpHandler` on the client

## Prerequisites

- .NET 9.0 SDK or later

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

1. **Server-Side Streaming**: Streaming large files from server to client
2. **Chunked Transfer**: Sending files in chunks to manage memory efficiently
3. **Metadata + Data Pattern**: Sending file metadata first, then data chunks
4. **Shared Memory Transport**: Ultra-low latency same-machine communication

See the main [Grpc.Net.SharedMemory](../../../src/Grpc.Net.SharedMemory/README.md) documentation for more details.
