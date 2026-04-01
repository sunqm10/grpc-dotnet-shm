#region Copyright notice and license

// Copyright 2025 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System.Buffers;
using System.Collections.Concurrent;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Batched frame writer inspired by Kestrel's <c>Http2FrameWriter</c>.
/// Uses a lock-free <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// for MPSC enqueue (multiple app threads → single writer thread) to avoid
/// <c>Monitor.Enter</c> contention in high-concurrency streaming scenarios.
/// Small payloads are defensively copied into pooled buffers at enqueue time.
/// Large payloads can be enqueued zero-copy; the pooled buffer is returned
/// after the data has been written to the ring buffer.
/// </summary>
internal sealed class ShmFrameWriter : IDisposable
{
    private struct FrameEntry
    {
        public FrameType Type;
        public uint StreamId;
        public byte Flags;
        public int Length;
        public ReadOnlyMemory<byte> Payload; // payload data (both paths)
        public byte[]? ReturnToPool;          // buffer to return to ArrayPool after ring write
    }

    private readonly ShmRing _ring;
    private readonly ConcurrentQueue<FrameEntry> _queue;
    private readonly ManualResetEventSlim _readySignal;
    private int _waiting; // 1 if writer thread is blocked in Wait; accessed via Volatile.Read/Write
    private volatile bool _completed;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _ct;
    private bool _disposed;

    public ShmFrameWriter(ShmRing ring, CancellationTokenSource cts)
    {
        _ring = ring;
        _cts = cts;
        _ct = cts.Token;
        _queue = new ConcurrentQueue<FrameEntry>();
        _readySignal = new ManualResetEventSlim(false);

        _writerTask = Task.Factory.StartNew(
            WriterLoop, _ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Enqueues a frame by defensively copying the payload into a pooled buffer.
    /// The copy is written to the ring and the buffer returned to the pool by
    /// the dedicated writer thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void Enqueue(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        var len = payload.Length;
        byte[]? buf = null;
        ReadOnlyMemory<byte> mem = default;
        if (len > 0)
        {
            buf = ArrayPool<byte>.Shared.Rent(len);
            payload.CopyTo(buf);
            mem = buf.AsMemory(0, len);
        }

        if (_completed)
        {
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        _queue.Enqueue(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = len, Payload = mem, ReturnToPool = buf
        });

        // Wake the writer thread if it is blocked waiting for data.
        // Late enqueues (after _completed is set) are handled by the three
        // drain layers in WriterLoop + Dispose — no dequeue here to avoid
        // accidentally consuming another thread's frame from the queue head.
        if (Volatile.Read(ref _waiting) != 0 && !_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Enqueues a frame without copying the payload. The caller's
    /// <paramref name="pooledBuffer"/> is returned to <see cref="ArrayPool{T}"/>
    /// after the data has been written to the ring buffer.
    /// Pass <c>null</c> if the payload does not need to be returned.
    /// </summary>
    /// <remarks>
    /// On failure the caller retains ownership of <paramref name="pooledBuffer"/>;
    /// this method does NOT return it to the pool.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void EnqueueZeroCopy(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        _queue.Enqueue(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = payload.Length, Payload = payload,
            ReturnToPool = pooledBuffer
        });

        if (Volatile.Read(ref _waiting) != 0 && !_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private void WriterLoop()
    {
        const int maxBatch = 512;
        var batch = new FrameEntry[maxBatch];

        try
        {
            while (!_ct.IsCancellationRequested && !_completed)
            {
                // Phase 1: immediate dequeue
                if (_queue.TryDequeue(out batch[0]))
                {
                    var count = 1;
                    while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                        count++;
                    FlushBatch(batch, count);
                    continue;
                }

                // Phase 2: adaptive spin
                var spinWait = new SpinWait();
                var found = false;
                while (!spinWait.NextSpinWillYield)
                {
                    spinWait.SpinOnce(sleep1Threshold: -1);
                    if (_queue.TryDequeue(out batch[0]))
                    {
                        var count = 1;
                        while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                            count++;
                        FlushBatch(batch, count);
                        found = true;
                        break;
                    }
                }

                if (found) continue;

                // Phase 3: blocking wait (lost-wake-safe pattern)
                // Set _waiting BEFORE Reset to ensure writers see it and call Set().
                Volatile.Write(ref _waiting, 1);
                _readySignal.Reset();
                if (_queue.TryDequeue(out batch[0]))
                {
                    // Data arrived between Phase 2 and Reset — no need to wait.
                    Volatile.Write(ref _waiting, 0);
                    var count = 1;
                    while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                        count++;
                    FlushBatch(batch, count);
                    continue;
                }

                try
                {
                    _readySignal.Wait(_ct);
                }
                finally
                {
                    Volatile.Write(ref _waiting, 0);
                }
            }

            // Drain remaining entries after _completed is set.
            // This preserves the Channel semantics where items enqueued before
            // completion are still consumed. Without this, the last frames
            // (trailers, half-close, window updates) would be silently dropped.
            while (_queue.TryDequeue(out batch[0]))
            {
                var count = 1;
                while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                    count++;
                FlushBatch(batch, count);
            }
        }
        catch (OperationCanceledException) { }
        catch (RingClosedException) { }
    }

    private void FlushBatch(FrameEntry[] batch, int count)
    {
        try
        {
            // No lock needed: the writer thread is the sole consumer of the
            // queue and all ring writes go through it, so there is no
            // concurrent access to the ring.
            // Importantly, NOT holding a lock here means that if
            // FrameProtocol.WriteMessage blocks waiting for ring space
            // (ReserveWrite), we do not prevent other enqueue operations
            // from completing — avoiding a deadlock where WindowUpdate
            // frames cannot be enqueued while the ring is full.
            for (var i = 0; i < count; i++)
            {
                ref var entry = ref batch[i];
                var payload = entry.Payload.Span;

                if (entry.Type == FrameType.Message)
                {
                    var isLast = (entry.Flags & MessageFlags.More) == 0;
                    FrameProtocol.WriteMessage(_ring, entry.StreamId, payload, isLast, _ct);
                }
                else
                {
                    var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
                    FrameProtocol.WriteFrame(_ring, header, payload, _ct);
                }
            }
        }
        finally
        {
            // Always return pooled buffers, even if a ring write threw.
            for (var i = 0; i < count; i++)
            {
                if (batch[i].ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(batch[i].ReturnToPool!);
                batch[i] = default;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // 1. Stop accepting new entries and wake the writer thread.
            _completed = true;
            _readySignal.Set();

            // 2. Give the writer thread a chance to flush remaining entries.
            var writerDone = false;
            try
            {
                writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException)
            {
                writerDone = true; // task faulted — it's done
            }

            // 3. If the writer is still blocked (e.g. ring full), cancel to
            //    unblock it, then wait again for it to actually exit.
            if (!writerDone)
            {
                _cts.Cancel();
                _readySignal.Set(); // unblock if waiting again
                try
                {
                    writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch (AggregateException)
                {
                    writerDone = true;
                }
            }

            // 4. Drain remaining entries.
            if (writerDone)
            {
                while (_queue.TryDequeue(out var entry))
                {
                    if (entry.ReturnToPool != null)
                        ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
                }
            }

            _readySignal.Dispose();

            // 5. Final drain: catch any frames enqueued between step 4 and
            //    _readySignal.Dispose(). Concurrent Enqueue calls that passed
            //    the _completed check before it was set may still be in-flight.
            while (_queue.TryDequeue(out var lateEntry))
            {
                if (lateEntry.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(lateEntry.ReturnToPool);
            }
        }
    }
}
