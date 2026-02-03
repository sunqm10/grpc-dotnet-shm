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

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An HttpMessageHandler that routes gRPC requests over shared memory.
/// Use with GrpcChannel.ForAddress() by setting GrpcChannelOptions.HttpHandler.
/// </summary>
/// <example>
/// <code>
/// var handler = new ShmHandler("my_grpc_segment");
/// var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
/// {
///     HttpHandler = handler
/// });
/// var client = new Greeter.GreeterClient(channel);
/// </code>
/// </example>
[SupportedOSPlatform("windows")]
public sealed class ShmHandler : HttpMessageHandler
{
    private readonly string _segmentName;
    private readonly ShmConnection _connection;
    private readonly SemaphoreSlim _connectionLock;
    private bool _disposed;

    /// <summary>
    /// Creates a new ShmHandler that connects to the specified shared memory segment.
    /// </summary>
    /// <param name="segmentName">The name of the shared memory segment to connect to.</param>
    public ShmHandler(string segmentName)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _connection = ShmConnection.ConnectAsClient(segmentName);
        _connectionLock = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets the segment name this handler is connected to.
    /// </summary>
    public string SegmentName => _segmentName;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Create a new stream for this request
        var stream = _connection.CreateStream();

        try
        {
            // Extract gRPC metadata
            var method = request.RequestUri?.AbsolutePath ?? "/";
            var authority = request.RequestUri?.Authority ?? "localhost";
            var metadata = ExtractMetadata(request.Headers);
            var deadline = ExtractDeadline(request.Headers);

            // Send request headers
            await stream.SendRequestHeadersAsync(method, authority, metadata, deadline);

            // Send request body if present
            if (request.Content != null)
            {
                var bodyStream = await request.Content.ReadAsStreamAsync(cancellationToken);
                await SendMessagesAsync(stream, bodyStream, cancellationToken);
            }

            // Signal end of request
            await stream.SendHalfCloseAsync();

            // Wait for response headers
            var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken);

            // Create response with streaming content
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ShmResponseContent(stream),
                Version = new Version(2, 0)
            };

            // Add response headers
            if (responseHeaders.Metadata != null)
            {
                foreach (var kv in responseHeaders.Metadata)
                {
                    var values = kv.Values.Select(v => v is byte[] bytes
                        ? Convert.ToBase64String(bytes)
                        : v?.ToString() ?? "");
                    response.Headers.TryAddWithoutValidation(kv.Key, values);
                }
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stream.CancelAsync();
            throw;
        }
    }

    private static async Task SendMessagesAsync(ShmGrpcStream stream, Stream bodyStream, CancellationToken cancellationToken)
    {
        // gRPC message format: [compressed:1][length:4][data:length]
        var headerBuffer = new byte[5];

        while (true)
        {
            // Read message header
            var headerBytesRead = await ReadExactlyAsync(bodyStream, headerBuffer, cancellationToken);
            if (headerBytesRead == 0) break; // End of stream
            if (headerBytesRead < 5) throw new InvalidDataException("Incomplete gRPC message header");

            var compressed = headerBuffer[0] != 0;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(1));

            if (compressed)
            {
                throw new NotSupportedException("Compression not yet supported");
            }

            // Read message body
            var messageBuffer = new byte[length];
            var messageBytesRead = await ReadExactlyAsync(bodyStream, messageBuffer, cancellationToken);
            if (messageBytesRead < length) throw new InvalidDataException("Incomplete gRPC message body");

            await stream.SendMessageAsync(messageBuffer, cancellationToken);
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (bytesRead == 0) return totalRead;
            totalRead += bytesRead;
        }
        return totalRead;
    }

    private static Metadata? ExtractMetadata(HttpRequestHeaders headers)
    {
        var metadata = new Metadata();

        foreach (var header in headers)
        {
            // Skip pseudo-headers and standard HTTP headers
            if (header.Key.StartsWith(':') ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (header.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
                {
                    // Binary metadata
                    metadata.Add(new Metadata.Entry(header.Key, Convert.FromBase64String(value)));
                }
                else
                {
                    metadata.Add(new Metadata.Entry(header.Key, value));
                }
            }
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static DateTime? ExtractDeadline(HttpRequestHeaders headers)
    {
        if (headers.TryGetValues("grpc-timeout", out var values))
        {
            var timeout = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(timeout))
            {
                // Parse timeout format: <value><unit> where unit is H/M/S/m/u/n
                if (TryParseGrpcTimeout(timeout, out var duration))
                {
                    return DateTime.UtcNow + duration;
                }
            }
        }
        return null;
    }

    private static bool TryParseGrpcTimeout(string timeout, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(timeout) || timeout.Length < 2)
            return false;

        var unit = timeout[^1];
        if (!long.TryParse(timeout[..^1], out var value))
            return false;

        duration = unit switch
        {
            'H' => TimeSpan.FromHours(value),
            'M' => TimeSpan.FromMinutes(value),
            'S' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMilliseconds(value),
            'u' => TimeSpan.FromMicroseconds(value),
            'n' => TimeSpan.FromTicks(value / 100), // nanoseconds
            _ => TimeSpan.Zero
        };

        return duration > TimeSpan.Zero;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _connection.Dispose();
                _connectionLock.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// HttpContent implementation that reads response messages from a ShmGrpcStream.
/// </summary>
internal sealed class ShmResponseContent : HttpContent
{
    private readonly ShmGrpcStream _stream;

    public ShmResponseContent(ShmGrpcStream stream)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeToStreamAsync(stream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        // Write gRPC-format messages to the output stream
        await foreach (var message in _stream.ReceiveMessagesAsync(cancellationToken))
        {
            // gRPC message format: [compressed:1][length:4][data]
            var header = new byte[5];
            header[0] = 0; // Not compressed
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);

            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(message, cancellationToken);
        }

        // Add trailers
        if (_stream.Trailers != null)
        {
            // Trailers are sent as trailing headers in HTTP/2
            // For HTTP/1.1-style content, we need to handle this differently
            // The caller should check stream.Trailers after reading content
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false; // Streaming content, length unknown
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
