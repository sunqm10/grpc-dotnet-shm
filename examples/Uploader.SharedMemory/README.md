# File Upload - Shared Memory Transport Example

This example demonstrates client-side streaming for large file uploads over the shared memory transport.

## Features

- Client-side streaming for uploading large files
- Demonstrates chunked data transfer over shared memory
- Shows metadata + data streaming pattern

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

1. **Client-Side Streaming**: Streaming large files from client to server
2. **Chunked Transfer**: Sending files in chunks to manage memory efficiently
3. **Metadata + Data Pattern**: Sending file metadata first, then data chunks

## Shared Memory Transport

This example uses the shared memory transport instead of TCP for ultra-low latency
same-machine communication. This is particularly useful for large file transfers
between processes on the same machine, avoiding TCP overhead.

The transport is Windows-only for now.

See the main [Grpc.Net.SharedMemory](../../../src/Grpc.Net.SharedMemory/README.md) documentation for more details.
