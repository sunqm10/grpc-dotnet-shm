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

using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents a gRPC connection over shared memory between a client and server.
/// Manages the underlying shared memory segment and provides stream multiplexing.
/// </summary>
public sealed class ShmConnection : IDisposable, IAsyncDisposable
{
    private readonly Segment _segment;
    private readonly bool _isClient;
    private readonly ConcurrentDictionary<uint, ShmGrpcStream> _streams;
    private readonly CancellationTokenSource _disposeCts;
    private readonly Task _frameReaderTask;
    private uint _nextStreamId;
    private bool _disposed;
    private bool _goAwaySent;
    private bool _goAwayReceived;

    /// <summary>
    /// Gets the connection name (shared memory segment name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether this is a client-side connection.
    /// </summary>
    public bool IsClient => _isClient;

    /// <summary>
    /// Gets whether the connection has been closed.
    /// </summary>
    public bool IsClosed => _disposed || _goAwaySent || _goAwayReceived;

    /// <summary>
    /// Raised when a GoAway frame is received from the remote side.
    /// </summary>
    public event EventHandler<GoAwayEventArgs>? GoAwayReceived;

    /// <summary>
    /// Creates a new client-side connection by opening an existing shared memory segment.
    /// </summary>
    /// <param name="name">The name of the shared memory segment to connect to.</param>
    /// <returns>A new client connection.</returns>
    [SupportedOSPlatform("windows")]
    public static ShmConnection ConnectAsClient(string name)
    {
        var segment = Segment.Open(name);
        return new ShmConnection(name, segment, isClient: true);
    }

    /// <summary>
    /// Creates a new server-side connection by creating a shared memory segment.
    /// </summary>
    /// <param name="name">The name for the shared memory segment.</param>
    /// <param name="ringCapacity">The capacity of each ring buffer (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams (default: 100).</param>
    /// <returns>A new server connection.</returns>
    public static ShmConnection CreateAsServer(string name, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        var segment = Segment.Create(name, ringCapacity, maxStreams);
        return new ShmConnection(name, segment, isClient: false);
    }

    private ShmConnection(string name, Segment segment, bool isClient)
    {
        Name = name;
        _segment = segment;
        _isClient = isClient;
        _streams = new ConcurrentDictionary<uint, ShmGrpcStream>();
        _disposeCts = new CancellationTokenSource();

        // Client uses odd stream IDs (1, 3, 5, ...), server uses even (2, 4, 6, ...)
        _nextStreamId = isClient ? 1u : 2u;

        // Start background frame reader
        _frameReaderTask = Task.Run(FrameReaderLoopAsync);
    }

    /// <summary>
    /// Creates a new stream for a gRPC call.
    /// </summary>
    /// <returns>A new gRPC stream.</returns>
    public ShmGrpcStream CreateStream()
    {
        ThrowIfDisposed();
        ThrowIfGoAway();

        var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2; // Increment by 2, return previous value
        var stream = new ShmGrpcStream(streamId, this);

        if (!_streams.TryAdd(streamId, stream))
        {
            throw new InvalidOperationException($"Stream ID {streamId} already exists");
        }

        return stream;
    }

    /// <summary>
    /// Gets the ring buffer for sending data (client→server for client, server→client for server).
    /// </summary>
    internal ShmRing TxRing => _isClient ? _segment.RingA : _segment.RingB;

    /// <summary>
    /// Gets the ring buffer for receiving data (server→client for client, client→server for server).
    /// </summary>
    internal ShmRing RxRing => _isClient ? _segment.RingB : _segment.RingA;

    /// <summary>
    /// Sends a GoAway frame to initiate graceful shutdown.
    /// </summary>
    /// <param name="message">Optional debug message.</param>
    public void SendGoAway(string? message = null)
    {
        if (_goAwaySent) return;
        _goAwaySent = true;

        try
        {
            FrameProtocol.WriteGoAway(TxRing, GoAwayFlags.Draining, message, _disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Connection already closing
        }
    }

    /// <summary>
    /// Sends a Ping frame.
    /// </summary>
    public void SendPing()
    {
        ThrowIfDisposed();
        var pingData = BitConverter.GetBytes(Environment.TickCount64);
        FrameProtocol.WritePing(TxRing, 0, pingData, _disposeCts.Token);
    }

    internal void RemoveStream(uint streamId)
    {
        _streams.TryRemove(streamId, out _);
    }

    internal void SendFrame(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
        FrameProtocol.WriteFrame(TxRing, header, payload, _disposeCts.Token);
    }

    private async Task FrameReaderLoopAsync()
    {
        try
        {
            while (!_disposeCts.Token.IsCancellationRequested)
            {
                // Read frame from receive ring
                var (header, payload) = await Task.Run(() =>
                    FrameProtocol.ReadFrame(RxRing, _disposeCts.Token), _disposeCts.Token);

                await ProcessFrameAsync(header, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Log error and close connection
            System.Diagnostics.Debug.WriteLine($"Frame reader error: {ex.Message}");
        }
    }

    private Task ProcessFrameAsync(FrameHeader header, byte[] payload)
    {
        switch (header.Type)
        {
            case FrameType.Headers:
            case FrameType.Message:
            case FrameType.Trailers:
            case FrameType.HalfClose:
            case FrameType.Cancel:
                // Route to stream
                if (_streams.TryGetValue(header.StreamId, out var stream))
                {
                    stream.OnFrameReceived(header, payload);
                }
                break;

            case FrameType.Ping:
                // Respond with Pong
                FrameProtocol.WritePong(TxRing, header.Flags, payload, _disposeCts.Token);
                break;

            case FrameType.Pong:
                // Ping response received - could track latency
                break;

            case FrameType.GoAway:
                _goAwayReceived = true;
                var message = payload.Length > 0 ? System.Text.Encoding.UTF8.GetString(payload) : null;
                GoAwayReceived?.Invoke(this, new GoAwayEventArgs(header.Flags, message));
                break;

            case FrameType.WindowUpdate:
                // Route window update to stream
                if (_streams.TryGetValue(header.StreamId, out var windowStream))
                {
                    var increment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    windowStream.OnWindowUpdate(increment);
                }
                break;
        }

        return Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfGoAway()
    {
        if (_goAwaySent || _goAwayReceived)
        {
            throw new InvalidOperationException("Connection is being closed due to GoAway");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Send GoAway if not already sent
            if (!_goAwaySent)
            {
                try { SendGoAway("Connection disposed"); } catch { }
            }

            _disposeCts.Cancel();

            // Wait for reader to finish
            try { _frameReaderTask.Wait(TimeSpan.FromSeconds(1)); } catch { }

            // Dispose all streams
            foreach (var stream in _streams.Values)
            {
                try { stream.Dispose(); } catch { }
            }
            _streams.Clear();

            _segment.Dispose();
            _disposeCts.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Send GoAway if not already sent
            if (!_goAwaySent)
            {
                try { SendGoAway("Connection disposed"); } catch { }
            }

            _disposeCts.Cancel();

            // Wait for reader to finish
            try { await _frameReaderTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            // Dispose all streams
            foreach (var stream in _streams.Values)
            {
                try { await stream.DisposeAsync(); } catch { }
            }
            _streams.Clear();

            _segment.Dispose();
            _disposeCts.Dispose();
        }
    }
}

/// <summary>
/// Event arguments for GoAway events.
/// </summary>
public sealed class GoAwayEventArgs : EventArgs
{
    /// <summary>
    /// Gets the GoAway flags.
    /// </summary>
    public byte Flags { get; }

    /// <summary>
    /// Gets the optional debug message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Creates new GoAwayEventArgs.
    /// </summary>
    public GoAwayEventArgs(byte flags, string? message)
    {
        Flags = flags;
        Message = message;
    }
}
