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
using NUnit.Framework;
using Grpc.Core;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// End-to-end tests demonstrating full gRPC-style request/response over shared memory.
/// </summary>
[TestFixture]
public class EndToEndTests
{
    [Test]
    [Timeout(10000)]
    public async Task UnaryCall_SimpleRequestResponse_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            Assert.That(serverStream!.RequestHeaders, Is.Not.Null);
            Assert.That(serverStream.RequestHeaders!.Method, Is.EqualTo("/greet.Greeter/SayHello"));
            Assert.That(serverStream.RequestHeaders.Metadata.Count, Is.EqualTo(1));

            byte[]? request = null;
            await foreach (var message in serverStream.ReceiveMessagesAsync())
            {
                request = message;
            }

            Assert.That(request, Is.Not.Null);

            await serverStream.SendResponseHeadersAsync();
            var name = Encoding.UTF8.GetString(request!);
            var responseMessage = Encoding.UTF8.GetBytes($"Hello, {name}!");
            await serverStream.SendMessageAsync(responseMessage);
            await serverStream.SendTrailersAsync(StatusCode.OK, "Success");
        });

        var clientStream = clientConnection.CreateStream();
        var metadata = new Metadata { { "client-id", "test-client" } };
        await clientStream.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost", metadata);
        var requestMessage = Encoding.UTF8.GetBytes("GreeterClient");
        await clientStream.SendMessageAsync(requestMessage);
        await clientStream.SendHalfCloseAsync();

        var headers = await clientStream.ReceiveResponseHeadersAsync();
        Assert.That(headers, Is.Not.Null);

        var responses = new List<string>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            responses.Add(Encoding.UTF8.GetString(msg));
        }

        await serverTask;

        Assert.That(responses, Is.EqualTo(new[] { "Hello, GreeterClient!" }));
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task ServerStreaming_MultipleMessages_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 5;
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            await serverStream.SendResponseHeadersAsync();
            for (int i = 0; i < messageCount; i++)
            {
                var message = Encoding.UTF8.GetBytes($"Message {i}");
                await serverStream.SendMessageAsync(message);
            }
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ServerStream", "localhost");
        await clientStream.SendHalfCloseAsync();

        _ = await clientStream.ReceiveResponseHeadersAsync();

        var responses = new List<string>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            responses.Add(Encoding.UTF8.GetString(msg));
        }

        await serverTask;

        Assert.That(responses.Count, Is.EqualTo(messageCount));
        Assert.That(responses[0], Is.EqualTo("Message 0"));
        Assert.That(responses[4], Is.EqualTo("Message 4"));
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task ClientStreaming_MultipleMessages_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 5;
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            var received = 0;
            await foreach (var _ in serverStream!.ReceiveMessagesAsync())
            {
                received++;
            }

            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes(received.ToString()));
            await serverStream.SendTrailersAsync(StatusCode.OK, $"Received {received} messages");
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ClientStream", "localhost");

        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Client message {i}");
            await clientStream.SendMessageAsync(message);
        }

        await clientStream.SendHalfCloseAsync();

        _ = await clientStream.ReceiveResponseHeadersAsync();
        var responses = new List<string>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            responses.Add(Encoding.UTF8.GetString(msg));
        }

        await serverTask;

        Assert.That(responses, Is.EqualTo(new[] { "5" }));
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task BidirectionalStreaming_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            await serverStream.SendResponseHeadersAsync();

            await foreach (var message in serverStream!.ReceiveMessagesAsync())
            {
                await serverStream.SendMessageAsync(message);
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/BiDi", "localhost");

        var requests = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var text = $"Request {i}";
            requests.Add(text);
            var message = Encoding.UTF8.GetBytes(text);
            await clientStream.SendMessageAsync(message);
        }

        await clientStream.SendHalfCloseAsync();

        _ = await clientStream.ReceiveResponseHeadersAsync();
        var echoes = new List<string>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            echoes.Add(Encoding.UTF8.GetString(msg));
        }

        await serverTask;

        Assert.That(echoes, Is.EqualTo(requests));
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task ErrorResponse_ReturnsStatusCode()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            await foreach (var _ in serverStream!.ReceiveMessagesAsync())
            {
            }

            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.InvalidArgument, "Missing required field");
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Error", "localhost");
        await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes("bad-request"));
        await clientStream.SendHalfCloseAsync();

        _ = await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var _ in clientStream.ReceiveMessagesAsync())
        {
        }

        await serverTask;

        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(clientStream.Trailers.GrpcStatusMessage, Is.EqualTo("Missing required field"));
    }

    [Test]
    [Timeout(10000)]
    public async Task Cancellation_CancelsStream()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Client creates and cancels stream
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        await clientStream.CancelAsync();

        // Assert
        Assert.That(clientStream.IsCancelled, Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task Deadline_PropagatesInHeaders()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            Assert.That(serverStream!.RequestHeaders, Is.Not.Null);
            Assert.That(serverStream.RequestHeaders!.DeadlineUnixNano, Is.GreaterThan(0UL));
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Deadline", "localhost", deadline: deadline);
        await clientStream.SendHalfCloseAsync();

        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.DeadlineUnixNano, Is.GreaterThan(0UL));
        await serverTask;
    }

    [Test]
    [Timeout(10000)]
    public async Task Metadata_RoundTrips()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var metadata = new Metadata
        {
            { "x-custom-header", "custom-value" },
            { "x-another-header", "another-value" }
        };

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            Assert.That(serverStream!.RequestHeaders, Is.Not.Null);
            Assert.That(serverStream.RequestHeaders!.Metadata.Count, Is.EqualTo(2));

            await foreach (var _ in serverStream.ReceiveMessagesAsync())
            {
            }

            var responseHeaders = new Metadata
            {
                { "x-server-header", "server-value" }
            };
            var responseTrailers = new Metadata
            {
                { "x-server-trailer", "trailer-value" }
            };

            await serverStream.SendResponseHeadersAsync(responseHeaders);
            await serverStream.SendTrailersAsync(StatusCode.OK, metadata: responseTrailers);
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();

        var headers = await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var _ in clientStream.ReceiveMessagesAsync())
        {
        }

        await serverTask;

        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Metadata.Count, Is.EqualTo(2));
        Assert.That(headers.Metadata.Any(kv => kv.Key == "x-server-header"), Is.True);
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.Metadata.Any(kv => kv.Key == "x-server-trailer"), Is.True);
    }

    [Test]
    [Timeout(10000)]
    public async Task LargeMessage_TransfersCorrectly()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var largeMessage = new byte[32 * 1024];
        new Random(42).NextBytes(largeMessage);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            byte[]? received = null;
            await foreach (var msg in serverStream!.ReceiveMessagesAsync())
            {
                received = msg;
            }

            Assert.That(received, Is.Not.Null);
            Assert.That(received, Is.EqualTo(largeMessage));

            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes("ok"));
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Large", "localhost");
        await clientStream.SendMessageAsync(largeMessage);
        await clientStream.SendHalfCloseAsync();

        _ = await clientStream.ReceiveResponseHeadersAsync();

        var responses = new List<string>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            responses.Add(Encoding.UTF8.GetString(msg));
        }

        await serverTask;

        Assert.That(responses, Is.EqualTo(new[] { "ok" }));
        Assert.That(clientStream.Trailers, Is.Not.Null);
        Assert.That(clientStream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    public void MultipleConnections_WorkIndependently()
    {
        // Arrange - two independent segments
        var segment1 = $"grpc_e2e_1_{Guid.NewGuid():N}";
        var segment2 = $"grpc_e2e_2_{Guid.NewGuid():N}";

        using var server1 = ShmConnection.CreateAsServer(segment1, ringCapacity: 4096, maxStreams: 100);
        using var server2 = ShmConnection.CreateAsServer(segment2, ringCapacity: 4096, maxStreams: 100);

        using var client1 = ShmConnection.ConnectAsClient(segment1);
        using var client2 = ShmConnection.ConnectAsClient(segment2);

        // Create streams on each
        var stream1 = client1.CreateStream();
        var stream2 = client2.CreateStream();

        // Assert - independent stream IDs
        Assert.That(stream1.StreamId, Is.EqualTo(1));
        Assert.That(stream2.StreamId, Is.EqualTo(1));
        Assert.That(client1.Name, Is.Not.EqualTo(client2.Name));
    }
}
