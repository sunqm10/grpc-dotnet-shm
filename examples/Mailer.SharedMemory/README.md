# Mailer.SharedMemory

This example demonstrates **bidirectional streaming** over shared memory transport.

## Description

The Mailer example shows a mailbox service where:
- The server simulates incoming mail to multiple mailboxes
- Clients can connect to their mailbox and receive real-time updates
- Clients can request to forward mail, and the server streams back the updated state
- Both client and server can send messages at any time (bidirectional streaming)

## Key Features

- **Bidirectional Streaming**: Both client and server send messages independently
- **Shared Memory Transport**: All communication uses shared memory for minimal latency
- **Real-time Updates**: Server pushes mail updates as they arrive
- **Multiple Mailboxes**: Multiple clients can connect to different mailboxes

## Running the Example

1. Start the server:
```bash
cd Server
dotnet run
```

2. In another terminal, start a client:
```bash
cd Client
dotnet run MyMailbox
```

3. The client will receive mail updates in real-time. Press any key to forward mail, or Escape to disconnect.

## Shared Memory Benefits

- **Zero-copy bidirectional communication**: Both request and response streams use shared memory rings
- **Low latency**: No network stack overhead, ideal for real-time notifications
- **Efficient multiplexing**: Multiple streams can share the same shared memory segment
