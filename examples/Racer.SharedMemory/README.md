# Racer.SharedMemory

This example demonstrates **concurrent streams** and high-throughput messaging over shared memory transport.

## Description

The Racer example is a benchmarking tool that measures message throughput:
- Client and server race to send as many messages as possible
- Both directions operate concurrently (bidirectional streaming)
- Measures messages per second in each direction
- Demonstrates the performance benefits of shared memory

## Key Features

- **Concurrent Streams**: Read and write operations happen simultaneously
- **High Throughput**: Designed to push maximum messages through the transport
- **Performance Metrics**: Reports messages/second for both directions
- **Shared Memory Benefits**: No network overhead for same-machine communication

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

3. The race runs for 30 seconds by default, showing real-time throughput.

## Expected Results

Shared memory transport should achieve significantly higher throughput than TCP:
- **TCP**: ~100,000 messages/second
- **Shared Memory**: ~1,000,000+ messages/second

The exact results depend on:
- CPU speed
- Message size
- Buffer configuration
