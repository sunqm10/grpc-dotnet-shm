# Error Handling - Shared Memory Transport Example

This example demonstrates error handling with rich error details over the shared memory transport.

## Features

- Uses `Google.Rpc.BadRequest` for rich error details
- Shows how to extract field violations from `RpcException`
- Demonstrates validation errors over shared memory

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

1. **Rich Error Details**: The server uses `Grpc.StatusProto` to attach detailed error information
2. **Validation Errors**: Shows how to validate request fields and return structured errors
3. **Client Error Handling**: Demonstrates extracting `BadRequest` details from `RpcException`

## Shared Memory Transport

This example uses the shared memory transport instead of TCP for ultra-low latency
same-machine communication. The transport is Windows-only for now.

See the main [Grpc.Net.SharedMemory](../../../src/Grpc.Net.SharedMemory/README.md) documentation for more details.
