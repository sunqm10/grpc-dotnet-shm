# Metadata Shared Memory Example

This example demonstrates metadata (headers/trailers) handling over shared memory transport.

It uses the canonical shared-memory gRPC pattern:
- Client: `GrpcChannel` + `ShmControlHandler`
- Server: `ShmGrpcServer` + generated protobuf stubs

Features demonstrated:
- Sending custom metadata from client
- Receiving metadata on server
- Sending response metadata (headers and trailers) from server
- Receiving response metadata on client
- Timestamp metadata for timing analysis

## Running

### Start the server

```bash
cd Server
dotnet run
```

### Start the client

```bash
cd Client
dotnet run
```

## Expected output

**Client:**
```
=== Unary Call with Metadata ===
Sending request with metadata:
	timestamp = Jan 15 15:30:45.1234567
	client-id = shm-client-1

Received response headers:
	timestamp = Jan 15 15:30:45.1234567
	server-location = shared-memory

Received response: this is examples/metadata

Received trailers:
	trailer-timestamp = Jan 15 15:30:45.1234567
```

**Server:**
```
Received request metadata:
	timestamp = Jan 15 15:30:45.1234567
	client-id = shm-client-1
Message: "this is examples/metadata", sending echo
```
