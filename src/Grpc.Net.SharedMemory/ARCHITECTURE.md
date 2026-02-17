# Grpc.Net.SharedMemory Transport Surface Layering

This note defines ownership boundaries for public transport surfaces.

## Canonical Surfaces

- Client: `ShmControlHandler`
- Server: `ShmGrpcServer`

These are the recommended entry points for application code and examples.

## Advanced / Low-Level Surfaces

- `ShmHandler`: legacy/advanced client handler kept for compatibility.
- `ShmHttpHandler`: advanced HTTP stack integration surface.
- `ShmConnectionListener`: low-level stream listener for custom hosts.
- `ShmControlListener`: control-plane connection listener used by `ShmGrpcServer` and custom hosts.

Application code should not start from these unless it needs custom transport integration.

## Layering

1. `ShmControlHandler` / `ShmGrpcServer` (canonical API layer)
2. `ShmControlListener` / `ShmConnection` / `ShmGrpcStream` (transport orchestration)
3. `Segment` / `ShmRing` / sync primitives (shared-memory data path)

The intent is to keep most code at layer 1 and reserve layers 2-3 for transport internals and advanced scenarios.