# Cancellation Shared Memory Example

This example demonstrates client-side cancellation of gRPC calls over shared memory transport using the idiomatic `UseSharedMemory()` / `ShmHttpHandler` integration.

## Design

Mirrors the Go grpc-go-shmem `examples/shm/features/cancellation` example.

The client:
- Creates a `GrpcChannel` with `ShmHttpHandler` for shared memory transport
- Starts a bidirectional streaming RPC using generated gRPC stubs
- Sends a few messages
- Cancels the `CancellationTokenSource` mid-stream
- Observes cancellation propagated to the server

The server:
- Uses `UseSharedMemory()` on the `WebHost` for shared memory transport
- Maps the `EchoService` with standard `MapGrpcService<T>()`
- Receives messages until cancellation
- Logs when cancellation is detected

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
Sending: message 1
Sending: message 2
Sending: message 3
cancelling context
Stream cancelled successfully
```

**Server:**
```
BidirectionalStreamingEcho: message 1
BidirectionalStreamingEcho: message 2
BidirectionalStreamingEcho: message 3
```
