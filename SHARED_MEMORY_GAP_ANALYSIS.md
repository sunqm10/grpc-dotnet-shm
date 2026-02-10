# Comprehensive Gap Analysis: .NET Shared Memory Transport

## Overview

This document provides a detailed review of the .NET shared memory transport implementation,
comparing against:
1. **grpc-go-shmem** ([github.com/markrussinovich/grpc-go-shmem](https://github.com/markrussinovich/grpc-go-shmem)) — The Go reference implementation
2. **gRPC .NET TCP transport** — For feature parity and test coverage equivalence

Last updated: 2026-02-07

---

## Current Status Summary

| Criterion | Rating | Details |
|-----------|--------|---------|
| **Minimal memory copies** | **A** | 1 copy per direction (send + receive), matching Go. Both use scatter-write reservations. Go's zero-copy read was disabled due to data corruption. |
| **Minimal kernel calls** | **A** | 0 kernel calls in steady state (adaptive spin). Same futex-based fallback as Go. |
| **Full gRPC integration** | **A-** | Client: `ShmHttpHandler` ✅. Server: `UseSharedMemory()` + `MapGrpcService<T>()` ✅. Two integration paths (native + Kestrel adapter). |
| **Feature parity with Go** | **A** | All Go features implemented. .NET exceeds Go in security tests (20+ vs 4), fallback tests (15 vs 4), keepalive tests (15 vs 1). Missing: 3 robustness tests. |
| **Test parity with TCP** | **B+** | ~372 tests across 32 files. All TCP functional test categories covered. Gaps: authorization, HttpContext, nested-call, misbehaved-peer tests. |
| **E2E example parity** | **A-** | All 11 Go examples have .NET counterparts (except multiplex + flow_control + gracefulstop). .NET has 10 additional examples. Some examples have gratuitous non-transport diffs. |

---

## 1. Memory Copies per Send/Receive

### Send Path (per message)

| Step | Operation | Copy? |
|------|-----------|-------|
| User provides `ReadOnlyMemory<byte>` (protobuf) | — | — |
| `ShmGrpcStream.SendMessageAsync` builds 5B gRPC prefix | — | No (tiny alloc) |
| `SendFrameScatterAsync` → `ShmConnection.SendFrame` | — | No (delegation) |
| `FrameProtocol.WriteFrame` → `ring.ReserveWrite(totalSize)` | Reserves mmap region | No copy |
| `WriteToReservation(headerBytes)` | 16B frame header → mmap | Trivial copy |
| `WriteToReservation(grpcPrefix)` | 5B prefix → mmap | Trivial copy |
| `WriteToReservation(protobufPayload)` | N bytes → mmap | **1 data copy** |
| `ring.CommitWrite()` | Publishes WriteIdx | No copy |

**Total: 1 meaningful copy (user bytes → shared memory). No intermediate buffers.**

### Receive Path (per message)

| Step | Operation | Copy? |
|------|-----------|-------|
| `FrameProtocol.ReadFramePooled` → `ring.ReserveRead(16)` | Header from mmap | No copy |
| `CopyFromReservation → stackalloc byte[16]` | 16B header → stack | Trivial copy |
| `ring.CommitRead(headerReservation)` | Frees header space | No copy |
| `ArrayPool<byte>.Shared.Rent(payloadLength)` | Pooled allocation | — |
| `ring.ReserveRead(payloadLength)` | Payload from mmap | No copy |
| `CopyFromReservation → pooledBuffer` | N bytes mmap → heap | **1 data copy** |
| `ring.CommitRead(payloadReservation)` | Frees payload space | No copy |
| Consumer receives `Memory<byte>` slice of pooled buffer | — | No further copy |

**Total: 1 meaningful copy (shared memory → pooled buffer). Consumer gets a direct slice.**

### Comparison with Go

| Metric | Go | .NET | Notes |
|--------|----|------|-------|
| Copies per send | **1** | **1** | Both use scatter-write into mmap reservations |
| Copies per receive | **1** | **1** | Go's zero-copy `readFrameView` was **disabled** (data corruption under high throughput). Both now copy. |
| Intermediate buffers | 0 | 0 | Both scatter-write header + prefix + payload |
| Receive allocation | `mem.Buffer` pool | `ArrayPool<byte>.Shared` | Both use pooling |

### Kestrel Adapter Path (additional copies)

When using the `UseSharedMemory()` Kestrel integration (`ShmStream.cs`), the data flows through `ShmRing.Read/Write` (the copy-based API, not the zero-copy `ReserveRead/ReserveWrite` API). This adds 1 copy per direction vs the native path and also adds HTTP/2 framing overhead. The native `ShmConnection` → `ShmGrpcStream` path is the recommended hot path.

---

## 2. Kernel Calls per Send/Receive

### Signaling Architecture

| Platform | Mechanism | Implementation |
|----------|-----------|----------------|
| **Linux** | `futex(2)` on shared-memory sequence counters | `LinuxRingSync.cs` — raw `SYS_futex(202)` via P/Invoke, `FUTEX_WAIT`/`FUTEX_WAKE` (cross-process, not `_PRIVATE`) |
| **Windows** | Named events | `WindowsRingSync.cs` |

### Wait Strategy (matches Go exactly)

1. **Adaptive spin** — up to `_dataSpinCutoff` iterations (default 300, range 50–2000) of `Volatile.Read` + `Thread.SpinWait(1)`. **0 kernel calls.**
2. **Futex fallback** — only if spin fails: increment `DataWaiters`/`SpaceWaiters`, re-check value, then `futex(FUTEX_WAIT, expectedSeq, 100ms timeout)`. **1 kernel call.**
3. **Signal path** — after write/commit: `Interlocked.Increment(DataSeq)`, then only if `DataWaiters > 0`: `futex(FUTEX_WAKE, 1)`. **0 or 1 kernel call.**

### Kernel Call Counts

| Scenario | Send | Receive | Total per RPC |
|----------|------|---------|---------------|
| **Best case** (steady state, data/space available) | **0** | **0** | **0** |
| **Moderate load** (spin usually succeeds) | 0–1 | 0–1 | 0–2 |
| **Cold path** (reader/writer sleeping) | 1–2 | 1–2 | 2–4 per side |

**Per unary RPC worst case: 8 total kernel calls (4 client + 4 server). Per unary RPC typical steady state: 0–2.**

This is identical to the Go implementation's adaptive-spin-then-futex strategy.

---

## 3. gRPC Integration

### Two Integration Paths

#### Path A: Native SHM Transport (lower overhead)
- **Server**: `ShmControlListener` → `ShmConnection` → `ShmGrpcStream` — custom framing, bypasses HTTP/2
- **Client**: `ShmHttpHandler` wraps `SocketsHttpHandler` with `ConnectCallback` that returns `ShmStream` over data segment
- **Result**: HTTP/2 tunneled over shared memory rings

#### Path B: Kestrel Adapter (idiomatic ASP.NET Core)
- **Server**: `UseSharedMemory("name")` registers `ShmConnectionListenerFactory` implementing `IConnectionListenerFactory`. Kestrel's HTTP/2 engine operates over `ShmStream`.
- **Client**: Same `ShmHttpHandler` as Path A
- **Result**: Standard `MapGrpcService<T>()` works unchanged

#### Comparison with Go

| Aspect | Go | .NET |
|--------|-----|------|
| Server integration | Replace `net.Listener` with `ShmListener` (**1 line**) | `UseSharedMemory()` on WebHost (**3 lines**) |
| Client integration | `grpc.WithShmTransport()` dial option (**2 lines**) | `ShmHttpHandler` in `GrpcChannelOptions` (**~6 lines**) |
| Lines to switch TCP→SHM | **~3 total** | **~9 total** |
| HTTP/2 involvement | None — custom transport replaces HTTP/2 entirely | HTTP/2 tunneled over SHM (Kestrel path) or custom framing (native path) |

The .NET approach requires ~3× more lines to switch, which is inherent to the ASP.NET Core / `HttpMessageHandler` API design, not a bug.

---

## 4. Feature Parity with Go

### Feature-by-Feature Comparison

| Feature | Go | .NET | Status |
|---------|:--:|:----:|--------|
| SPSC ring buffer with futex | ✅ | ✅ | ✅ Parity |
| Scatter-write (zero intermediate buffer) | ✅ | ✅ | ✅ Parity |
| Adaptive spin + futex wait | ✅ | ✅ | ✅ Parity |
| Frame protocol (16B header, wire-compatible) | ✅ | ✅ | ✅ Parity |
| Segment layout (128B header, `/dev/shm`) | ✅ | ✅ | ✅ Parity (byte-compatible) |
| Control segment handshake | ✅ | ✅ | ✅ Parity |
| All 4 streaming types | ✅ | ✅ | ✅ Parity |
| Flow control (connection + stream windows) | ✅ | ✅ | ✅ Parity |
| BDP estimation | ✅ | ✅ | ✅ Parity |
| GOAWAY / graceful draining | ✅ | ✅ | ✅ Parity |
| Keepalive (ping/pong + enforcement policy) | ✅ | ✅ | ✅ Parity |
| Max concurrent streams | ✅ | ✅ | ✅ Parity |
| Security handshake (PID-based, 3-step) | ✅ | ✅ | ✅ .NET has **more** coverage (20+ vs 4 tests) |
| SHM-aware transport selector | ✅ | ✅ | ✅ Different idiom, same function |
| TCP fallback | ✅ | ✅ | ✅ .NET has richer policy model |
| Compression | ✅ | ✅ | ✅ .NET has dedicated tests (18) |
| Retry / hedging | ✅ | ✅ | ✅ .NET has unit-level tests (19+) |
| Health service over SHM | — | ✅ | .NET-only extra |
| Diagnostics / telemetry | — | ✅ | .NET-only extra |
| Go↔.NET wire interop verification | — | ✅ | .NET-only (InteropTests.cs) |
| Stream scheduler (weighted fair queue) | ✅ | ❌ | Missing (low impact) |
| `shm://` URI resolver | ✅ | ❌ | Missing (uses segment name instead) |

### Areas Where .NET Exceeds Go

| Area | Go Tests | .NET Tests |
|------|:--------:|:----------:|
| Security handshake | 4 | **20+** |
| Fallback | 4 | **15** |
| Keepalive | 1 | **15** |
| Load balancing | ~3 | **19** |
| Compression | 0 (SHM-specific) | **18** |
| Health service | 0 | **14** |
| Diagnostics | 0 | **9** |
| Retry / hedging | 0 (unit) | **19+** |

---

## 5. Test Parity with TCP Tests

### Well-Covered (SHM test mirrors TCP functional test)

| TCP Functional Test | SHM Equivalent | SHM Tests |
|--------------------|----------------|:---------:|
| `Client/StreamingTests` | `ShmStreamingTests` + `StreamingEdgeCaseTests` | 21 |
| `Client/CompressionTests` | `ShmCompressionTests` + `ShmCompressionE2ETests` | 18+ |
| `Client/MaxMessageSizeTests` | `ShmMaxMessageSizeTests` | 7 |
| `Client/InterceptorTests` | `ShmInterceptorTests` | 7 |
| `Client/RetryTests` | `ShmRetryPolicyTests` | 19 |
| `Client/HedgingTests` | `ShmHedgingTests` | 8 |
| `Client/CancellationTests` | `CancellationTests` | 8 |
| `Client/DeadlineTests` | `DeadlineTests` | 11 |
| `Client/MetadataTests` | `MetadataTests` + `HeadersTrailersTests` | 15+ |
| `Server/CompressionTests` | `ShmCompressionE2ETests` | E2E |
| `Server/UnimplementedTests` | `ShmUnimplementedTests` | ~10 |
| `Server/LifetimeTests` | `ShmLifetimeTests` + `ConnectionLifecycleTests` | 13+ |
| `Server/DiagnosticsTests` | `ShmDiagnosticsTests` | 9 |

### Gaps Relative to TCP Functional Tests

| TCP Test | SHM Equivalent | Gap Severity |
|----------|---------------|:------------:|
| `Client/AuthorizationTests` | None (ShmSecurity covers handshake, not per-call authz) | **Medium** |
| `Server/HttpContextTests` | None (ASP.NET Core HttpContext interaction) | **Medium** |
| `Server/NestedTests` | None (re-entrant gRPC calls over SHM) | **Medium** |
| `Client/ClientFactoryTests` | None (`IHttpClientFactory` integration) | Low |
| `Client/EventSourceTests` | None (EventSource/ETW tracing) | Low |
| `Server/CorsTests` | N/A — CORS is HTTP-specific | Not applicable |

### Gaps Relative to Go SHM Tests

| Go Test | Description | .NET Equivalent | Priority |
|---------|-------------|----------------|:--------:|
| `TestShmStreamIDExhaustion` | Transport enters draining when IDs exhausted | **Missing** | **High** |
| `TestShmClientWithMisbehavedServer` | Robustness against malformed server | **Missing** | **High** |
| `TestShmServerWithMisbehavedClient` | Robustness against malformed client | **Missing** | **High** |
| `TestFutexSimpleTimeout` / `TestFutexWake` | Isolated futex unit tests | Missing (tested indirectly) | Low |
| `BenchmarkShmRingWriteRead` | In-project ring benchmarks | Missing (separate benchmark-shm/ dir) | Medium |

### Structural Issue: TransportTestBase

`TransportTestBase` supports a `TransportKind.Tcp` enum but throws `NotSupportedException`. The planned dual-transport test parameterization (same test over both SHM and TCP) is **not yet implemented**. Only `LinuxSmokeTests` and `ShmCompressionE2ETests` use this base class.

### Platform Note

Many SHM tests use `[Platform("Win")]` but not Linux. Given that Linux (`/dev/shm` + futex) is the primary target, more tests should run under `[Platform("Linux")]` or be platform-agnostic.

---

## 6. E2E Example Parity

### Go-to-.NET Example Mapping

| Go Example (`examples/shm/`) | .NET Example | Status |
|-------------------------------|-------------|:------:|
| `helloworld/` | `Greeter.SharedMemory/` | ✅ |
| `route_guide/` | `RouteGuide.SharedMemory/` | ✅ |
| `features/cancellation/` | `Cancellation.SharedMemory/` | ✅ |
| `features/deadline/` | `Deadline.SharedMemory/` | ✅ |
| `features/interceptor/` | `Interceptor.SharedMemory/` | ✅ |
| `features/metadata/` | `Metadata.SharedMemory/` | ✅ |
| `features/keepalive/` | `Keepalive.SharedMemory/` | ✅ |
| `features/compression/` | `Compression.SharedMemory/` | ✅ |
| `features/error_handling/` | `Error.SharedMemory/` | ✅ |
| `features/error_details/` | `Error.SharedMemory/` | ✅ |
| `features/retry/` | `Retrier.SharedMemory/` | ✅ |
| `features/flow_control/` | **Missing** | ❌ |
| `features/gracefulstop/` | **Missing** | ❌ |
| `features/multiplex/` | **Missing** | ❌ |

**.NET also has 10 additional examples** with no Go counterpart: Counter, Echo, Channeler, Downloader, Mailer, Progressor, Racer, Reflector, Uploader, Vigor.

### Dialer-Only Diff Requirement

In Go, switching TCP → SHM requires **~3 lines** changed:
```go
// Server: swap listener
lis, _ := transport.NewShmListener(...)  // was: net.Listen("tcp", ...)
// Client: add dial option
conn, _ := grpc.NewClient("shm://name", grpc.WithShmTransport())
```

In .NET, the minimum is **~9 lines**:
```csharp
// Server (+3 lines): add using, UseSharedMemory, RunAsync
using Grpc.AspNetCore.Server.SharedMemory;
builder.WebHost.UseSharedMemory("segment_name");
await app.RunAsync();  // was: app.Run()

// Client (+6 lines): add using, constant, handler in options
using Grpc.Net.SharedMemory;
const string SegmentName = "segment_name";
var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions {
    HttpHandler = new ShmHttpHandler(SegmentName), DisposeHttpClient = true
});
```

This ~3× difference is inherent to the .NET API design.

### Example Cleanliness

**Greeter.SharedMemory** is the gold standard — it differs from the TCP `Greeter` example **only in transport configuration**. However, other examples (Counter, Uploader, others) have **gratuitous non-transport changes**: added console banners, extra try/catch blocks, logging changes, different constant values, refactored service code. These should be cleaned up so every SHM example is identical to its TCP counterpart except for transport plumbing, matching the Go philosophy.

**Echo.SharedMemory** is **incomplete** — only has `Proto/` directory, no Client/ or Server/.

---

## Detailed Reference Data

### Test Coverage Summary

| Category | Tests | File |
|----------|:-----:|------|
| Fallback | 25 | ShmFallbackTests |
| Security | 24 | ShmSecurityTests |
| Load Balancing | 19 | ShmLoadBalancingTests |
| Retry | 19 | ShmRetryPolicyTests |
| Compression | 18 | ShmCompressionTests |
| Metadata | 15 | MetadataTests |
| Status Codes | 15 | StatusCodeTests |
| Keepalive | 15 | KeepaliveTests |
| Connection Lifecycle | 13 | ConnectionLifecycleTests |
| Streaming Edge Cases | 13 | StreamingEdgeCaseTests |
| Health Service | 13 | ShmHealthServiceTests |
| Lifetime | 13 | ShmLifetimeTests |
| Deadline | 11 | DeadlineTests |
| BDP Estimation | 11 | BdpEstimatorTests |
| End-to-End | 10 | EndToEndTests |
| Ring Buffer | 10 | RingBufferTests |
| Segment | 9 | SegmentTests |
| Diagnostics | 9 | ShmDiagnosticsTests |
| Frame Protocol | 8 | FrameProtocolTests |
| Streaming | 8 | ShmStreamingTests |
| gRPC Stream | 8 | ShmGrpcStreamTests |
| Hedging | 8 | ShmHedgingTests |
| Cancellation | 8 | CancellationTests |
| Interceptor | 7 | ShmInterceptorTests |
| Max Message Size | 7 | ShmMaxMessageSizeTests |
| Unimplemented | ~10 | ShmUnimplementedTests |
| Connection | 6 | ShmConnectionTests |
| Headers/Trailers | 10 | HeadersTrailersTests |
| Flow Control | 8 | FlowControlTests |
| Interop (Go↔.NET) | 12 | InteropTests |
| Linux Smoke | 3 | LinuxSmokeTests |
| Compression E2E | 6 | ShmCompressionE2ETests |
| **Total** | **~372** | **32 test files** |

---

## Action Items (Prioritized)

### High Priority

| # | Item | Effort |
|---|------|--------|
| 1 | **Add misbehaved peer tests** — `TestShmClientWithMisbehavedServer` and `TestShmServerWithMisbehavedClient` equivalents from Go's `shm_coverage_test.go` | Medium |
| 2 | **Add stream ID exhaustion test** — verify transport enters draining mode when stream IDs are exhausted (Go: `TestShmStreamIDExhaustion`) | Medium |
| 3 | **Clean up example diffs** — Counter, Uploader, and other SHM examples should differ from TCP counterparts only in transport configuration | Easy |
| 4 | **Complete Echo.SharedMemory** — add Client/ and Server/ directories or remove the incomplete example | Easy |

### Medium Priority

| # | Item | Effort |
|---|------|--------|
| 5 | **Add missing Go examples** — flow_control, gracefulstop, multiplex | Easy |
| 6 | **Add HttpContext and nested-call tests** — gaps vs TCP `Server/HttpContextTests` and `Server/NestedTests` | Medium |
| 7 | **Implement dual-transport test parameterization** — wire `TransportTestBase` TCP path to create real Kestrel server so same tests run over both SHM and TCP | Hard |
| 8 | **Add in-project .NET ring benchmarks** — for regression tracking (currently only Go benchmarks in `benchmark-shm/`) | Easy |

### Low Priority

| # | Item | Effort |
|---|------|--------|
| 9 | **Add isolated futex/signaling unit tests** — currently tested only indirectly through ring buffer tests | Easy |
| 10 | **Add EventSource tracing tests** — gap vs TCP `Client/EventSourceTests` | Medium |
| 11 | **Implement stream scheduler** — weighted fair queueing (Go has it, low practical impact) | Hard |
| 12 | **Implement `shm://` URI resolver** — UX improvement, not a functional gap | Easy |
