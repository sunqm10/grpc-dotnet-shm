# Channeler.SharedMemory

This example demonstrates **multi-threaded** gRPC operations over shared memory transport.

## Description

The Channeler example shows concurrent data operations:
- Multiple threads uploading data simultaneously (client streaming)
- Multiple threads downloading data simultaneously (server streaming)
- Thread-safe shared memory communication
- Demonstrates shared memory handles concurrent access correctly

## Key Features

- **Multi-Threaded Client**: 5 concurrent operations (3 uploads + 2 downloads)
- **Multi-Threaded Server**: Each stream handled on separate thread
- **Client Streaming**: UploadData sends chunks and receives final result
- **Server Streaming**: DownloadResults receives request and streams responses
- **Thread Safety**: Shared memory ring buffers handle concurrent access

## Running the Example

1. Start the server:
```bash
cd Server
dotnet run
```

2. In another terminal, start a client:
```bash
cd Client
dotnet run
```

3. Watch the interleaved output from multiple threads.

## Shared Memory Benefits

- **Thread-safe**: Ring buffer design supports concurrent readers/writers
- **Low contention**: Per-stream buffers reduce lock contention
- **High throughput**: No serialization overhead between threads
