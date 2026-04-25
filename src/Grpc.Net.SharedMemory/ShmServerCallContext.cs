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

using System.Text;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A <see cref="ServerCallContext"/> implementation for the shared memory transport.
/// Wraps an <see cref="ShmGrpcStream"/> and provides gRPC call context to service methods.
/// </summary>
internal sealed class ShmServerCallContext : ServerCallContext
{
    private readonly ShmGrpcStream _stream;
    private readonly HeadersV1 _requestHeaders;
    private readonly CancellationToken _cancellationToken;
    private readonly Metadata _requestMetadata;
    private readonly Metadata _responseTrailers;
    private Status _status;
    private WriteOptions? _writeOptions;
    private bool _headersSent;

    public ShmServerCallContext(ShmGrpcStream stream, HeadersV1 requestHeaders, CancellationToken cancellationToken)
    {
        _stream = stream;
        _requestHeaders = requestHeaders;
        _cancellationToken = cancellationToken;
        _requestMetadata = ConvertFromShmMetadata(requestHeaders.Metadata);
        _responseTrailers = new Metadata();
        _status = Status.DefaultSuccess;
    }

    /// <summary>
    /// Ensures response headers have been sent. Called before first response message.
    /// </summary>
    internal Task EnsureResponseHeadersSentAsync()
    {
        if (_headersSent)
            return Task.CompletedTask;

        return EnsureResponseHeadersSentSlowAsync();
    }

    internal bool HeadersSent => _headersSent;
    internal void MarkHeadersSent() => _headersSent = true;

    private async Task EnsureResponseHeadersSentSlowAsync()
    {
        _headersSent = true;
        // grpc-encoding is injected by ShmGrpcStream.InjectResponseEncoding()
        // automatically in all header-sending paths.
        await _stream.SendResponseHeadersAsync();
    }

    protected override string MethodCore => _requestHeaders.Method ?? "";
    protected override string HostCore => _requestHeaders.Authority ?? "localhost";
    protected override string PeerCore => $"shm://{_stream.StreamId}";

    protected override DateTime DeadlineCore
    {
        get
        {
            if (_requestHeaders.DeadlineUnixNano == 0)
                return DateTime.MaxValue;
            // DeadlineUnixNano is Unix time in nanoseconds
            var unixMs = (long)(_requestHeaders.DeadlineUnixNano / 1_000_000);
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        }
    }

    protected override Metadata RequestHeadersCore => _requestMetadata;
    protected override CancellationToken CancellationTokenCore => _rpcCancellationToken ?? _cancellationToken;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }

    /// <summary>
    /// The RPC-level cancellation token (linked to server shutdown + deadline).
    /// Set by <see cref="CreateLinkedCancellationSource"/>. Before that call,
    /// CancellationTokenCore falls back to the server shutdown token.
    /// </summary>
    private CancellationToken? _rpcCancellationToken;

    /// <summary>
    /// Whether the RPC deadline has been reached. Set by the linked
    /// CancellationTokenSource created in <see cref="CreateLinkedCancellationSource"/>.
    /// </summary>
    internal bool DeadlineReached { get; private set; }

    /// <summary>
    /// Creates a <see cref="CancellationTokenSource"/> linked to the server
    /// shutdown token and the RPC deadline (if set). The returned CTS should
    /// be disposed by the caller after the RPC completes.
    /// </summary>
    internal CancellationTokenSource CreateLinkedCancellationSource()
    {
        var deadline = DeadlineCore;
        if (deadline == DateTime.MaxValue)
        {
            // No deadline — just link to server shutdown
            var cts0 = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            _rpcCancellationToken = cts0.Token;
            return cts0;
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            // Already expired
            DeadlineReached = true;
            var expired = new CancellationTokenSource();
            expired.Cancel();
            _rpcCancellationToken = expired.Token;
            return expired;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        cts.CancelAfter(remaining);
        cts.Token.Register(() =>
        {
            if (!_cancellationToken.IsCancellationRequested)
                DeadlineReached = true;
        });
        _rpcCancellationToken = cts.Token;
        return cts;
    }

    protected override AuthContext AuthContextCore =>
        new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotSupportedException("Context propagation is not supported on shared memory transport");
    }

    protected override async Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        if (_headersSent)
            throw new InvalidOperationException("Response headers already sent");
        _headersSent = true;
        await _stream.SendResponseHeadersAsync(responseHeaders);
    }

    /// <summary>
    /// Converts SHM metadata (MetadataKV[]) to gRPC Metadata.
    /// </summary>
    internal static Metadata ConvertFromShmMetadata(IReadOnlyList<MetadataKV> shmMetadata)
    {
        var metadata = new Metadata();
        foreach (var kv in shmMetadata)
        {
            foreach (var value in kv.Values)
            {
                if (kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
                {
                    metadata.Add(new Metadata.Entry(kv.Key, value));
                }
                else
                {
                    metadata.Add(new Metadata.Entry(kv.Key, Encoding.UTF8.GetString(value)));
                }
            }
        }
        return metadata;
    }
}
