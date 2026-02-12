# Shared Memory Transport: Go Parity Plan

This document tracks the work to bring the .NET shared memory transport implementation
to parity with [grpc-go-shmem](https://github.com/markrussinovich/grpc-go-shmem).

## Status Key
- [ ] Not started
- [x] Completed

---

## 1. Eliminate Extra Memory Copies (Write Path)

**Current**: 2 copies per send (user→heap gRPC envelope, heap→mmap ring)
**Go baseline**: 1 copy per send (data→mmap ring directly)
**Target**: 1 copy per send

**Changes**:
- `ShmGrpcStream.SendMessageAsync`: Write the 5-byte gRPC length-prefix directly into
  the `WriteReservation` slices instead of allocating `new byte[5 + data.Length]`
- `FrameProtocol.WriteFrame`: Accept `ReadOnlyMemory<byte>` prefix + payload separately
  so the caller can avoid the intermediate allocation

**Verification**: `EndToEndTests`, `ShmStreamingTests`, `FrameProtocolTests`, all examples

- [x] Eliminate intermediate byte[] in SendMessageAsync
- [x] Verify all tests pass

## 2. Eliminate Extra Memory Copies (Read Path)

**Current**: 2 copies per receive (mmap→heap via `new byte[]`, heap→sliced heap via `[5..]`)
**Go baseline**: 0 copies for contiguous data (returns `mem.Buffer` backed by mmap)
**Target**: 0-1 copies per receive

**Changes**:
- `FrameProtocol.ReadFrame`: Return `ReadOnlyMemory<byte>` backed by the ring reservation
  instead of copying to `new byte[header.Length]` for the common (contiguous) case
- `ShmGrpcStream.ReceiveMessagesAsync`: Use `ReadOnlyMemory<byte>.Slice(5)` instead of
  `payload[5..]` array indexer to avoid the copy
- Implement lifetime management: the `ReadCommit` must be deferred until the consumer
  is done with the buffer

**Verification**: `EndToEndTests`, `ShmStreamingTests`, `RingBufferTests`

- [x] Zero-copy read for contiguous payloads
- [x] Eliminate array slice copy in ReceiveMessagesAsync
- [x] Verify all tests pass

## 3. Transport-Parameterized Test Runner

**Current**: Separate test files manually mirror TCP functional tests
**Go baseline**: `listTestEnv()` runs ALL e2e tests over both TCP and shm automatically
**Target**: Shared test infrastructure that runs TCP tests over shm

**Changes**:
- Create a `TransportTestBase` or `[TestFixture]` parameterization that runs key
  functional test scenarios over both TCP and shm transports
- Ensure new TCP tests automatically cover shm

**Verification**: All parameterized tests pass on both transports

- [x] Create parameterized test infrastructure
- [x] Port key TCP functional tests to the shared runner
- [x] Verify all tests pass

## 4. Standardize Example Dialer Pattern

**Current**: Some examples use `ShmHandler` + `GrpcChannel`, others use raw `ShmConnection`
**Go baseline**: All examples use `grpc.WithShmTransport()` uniformly
**Target**: All .NET examples use `ShmHandler`/`ShmControlHandler` + `GrpcChannel.ForAddress`

**Changes**:
- Update RouteGuide.SharedMemory, Cancellation.SharedMemory, Deadline.SharedMemory,
  Metadata.SharedMemory, Keepalive.SharedMemory to use `ShmHandler` + `GrpcChannel`
- Ensure the only difference from the TCP example is the handler attribute

**Verification**: All updated examples build and run correctly

- [ ] Update RouteGuide.SharedMemory
- [ ] Update Cancellation.SharedMemory
- [ ] Update Deadline.SharedMemory
- [ ] Update Metadata.SharedMemory
- [ ] Update Keepalive.SharedMemory
- [ ] Verify all examples work

## 5. Wire Compression into Data Path

**Current**: `IShmCompressor` interface + gzip/deflate/identity impls exist but are not
connected — `SendMessageAsync` always sets `compressed=0`
**Go baseline**: Compression works transparently via standard gRPC layer
**Target**: Compression wired into send/receive

**Changes**:
- `ShmGrpcStream.SendMessageAsync`: Apply configured compressor before writing
- `ShmGrpcStream.ReceiveMessagesAsync`: Decompress on read when `compressed=1`
- Wire `CompressionOptions` into `ShmConnection` / `ShmGrpcStream`

**Verification**: `ShmCompressionTests` (end-to-end), `Compression.SharedMemory` example

- [x] Wire compression into SendMessageAsync
- [x] Wire decompression into ReceiveMessagesAsync
- [x] Verify tests and example pass

## 6. Wire Retry into Data Path

**Current**: `ShmRetryPolicy` + `ShmRetryThrottling` exist but no call site invokes them
**Go baseline**: Retry works transparently via standard gRPC layer
**Target**: Retry logic connected to the RPC call path

**Changes**:
- Integrate `ShmRetryPolicy` into `ShmHandler`/`ShmControlHandler` `SendAsync`
- Apply exponential backoff + throttling on retryable status codes

**Verification**: `ShmRetryPolicyTests`, `ShmHedgingTests`, `Retrier.SharedMemory` example

- [ ] Wire retry into ShmHandler/ShmControlHandler
- [ ] Verify tests and example pass

## 7. Add TCP Fallback & Mixed Transport

**Current**: Not implemented
**Go baseline**: `dialShmWithFallback` tries shm, falls back to TCP; `AllowMixedTransport`
handles both shm:// and tcp:// addresses
**Target**: Equivalent fallback support in .NET

**Changes**:
- Add `ShmTransportOptions.FallbackEnabled` and `TcpFallbackAddress`
- In `ShmControlHandler.SendAsync`: if shm connection fails and fallback is enabled,
  delegate to a standard `HttpClientHandler`
- Add `AllowMixedTransport` to handle non-shm addresses

**Verification**: New `ShmFallbackTests`, manual example test

- [ ] Implement TCP fallback in ShmControlHandler
- [ ] Implement mixed transport support
- [ ] Add tests
- [ ] Verify all tests pass

## 8. Add Security Handshaker

**Current**: No authentication/encryption on the shm transport
**Go baseline**: `ShmSecurityHandshaker` performs a security handshake over rings
**Target**: Optional security handshake support

**Changes**:
- Implement `IShmSecurityHandshaker` interface
- Wire into `ShmControlHandler` and `ShmControlListener` connection setup
- Support auth info propagation to the gRPC context

**Verification**: New `ShmSecurityTests`

- [ ] Design IShmSecurityHandshaker interface
- [ ] Implement handshaker
- [ ] Wire into connection setup
- [ ] Add tests
- [ ] Verify all tests pass

## 9. Add Automated Cross-Process Test

**Current**: Binary-layout interop tests exist but no automated cross-process test
**Go baseline**: `shm_cross_process_test.go` spawns child processes automatically
**Target**: NUnit test that spawns server + client processes and validates round-trip

**Changes**:
- Create a test that builds and runs the InteropServer and InteropClient
  as separate processes
- Validate that messages round-trip correctly across process boundaries

**Verification**: New test passes in CI

- [ ] Create cross-process test
- [ ] Verify test passes
