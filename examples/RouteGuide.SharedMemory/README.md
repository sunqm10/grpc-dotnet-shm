# RouteGuide Shared Memory Example

This example demonstrates all four gRPC RPC types using shared memory transport with the idiomatic `UseSharedMemory()` / `ShmHttpHandler` integration:

- **Unary RPC**: `GetFeature` - Get a feature at a given point
- **Server Streaming RPC**: `ListFeatures` - List features within a rectangle
- **Client Streaming RPC**: `RecordRoute` - Record a route and get a summary
- **Bidirectional Streaming RPC**: `RouteChat` - Send and receive route notes

## Design

This example matches the design of the Go grpc-go-shmem `examples/shm/route_guide` example.

The server uses `UseSharedMemory()` on the `WebHost` and maps the `RouteGuideService` with standard `MapGrpcService<T>()`. The client uses `ShmHttpHandler` with a standard `GrpcChannel` and generated gRPC stubs.

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
=== GetFeature (Unary RPC) ===
Feature: name: "Patriots Path, Mendham, NJ 07945, USA", point:(409146138, -746188906)
Feature: name: "", point:(0, 0)

=== ListFeatures (Server Streaming RPC) ===
Looking for features between 40, -75 and 42, -73
  Feature: "Patriots Path, Mendham, NJ 07945, USA" at (409146138, -746188906)
  ...

=== RecordRoute (Client Streaming RPC) ===
Traversing 10 points.
Route summary: point_count:10 feature_count:4 distance:... elapsed_time:...

=== RouteChat (Bidirectional Streaming RPC) ===
Got message "First message" at point(0, 1)
...

All examples completed successfully!
```
