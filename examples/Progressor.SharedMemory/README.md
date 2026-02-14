# Progressor.SharedMemory

This example demonstrates **progress reporting** over shared memory transport using server streaming.

## Description

The Progressor example shows how to report progress for long-running operations:
- Client initiates a long-running operation
- Server streams progress updates (0%, 10%, 20%, ... 100%)
- Client displays a real-time progress bar
- Final result is sent when the operation completes
- All communication uses shared memory for minimal latency

## Key Features

- **Server Streaming**: Server pushes multiple progress messages
- **Progress Pattern**: Uses `oneof` to distinguish progress updates from final results
- **Real-time UI**: Client shows animated progress bar
- **Shared Memory Benefits**: Low-latency progress updates

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

3. Watch the progress bar update in real-time as the server processes data.

## Protocol Design

The proto uses a `oneof` field to send either progress updates or the final result:

```protobuf
message HistoryResponse {
  oneof ResponseType {
    int32 progress = 1;
    HistoryResult result = 2;
  }
}
```

## Shared Memory Benefits

- **Low Latency Updates**: Progress updates arrive with minimal delay
- **No Network Jitter**: Consistent timing for progress updates
- **Efficient for Same-Machine**: Ideal for local processing tasks
