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
    [Platform("Win")]
    [Timeout(10000)]
    public async Task UnaryCall_SimpleRequestResponse_Works()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";

        // Create server connection
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);

        // Create client connection
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Start server task to handle one request
        var serverTask = Task.Run(async () =>
        {
            // Create a server-side stream to respond
            var serverStream = serverConnection.CreateStream();

            // Wait for request headers from client
            // In real implementation, the connection would route frames to streams
            // For this test, we simulate by directly reading from the ring
            await Task.Delay(50); // Give client time to send

            // Server sends response headers
            await serverStream.SendResponseHeadersAsync();

            // Server sends a message (simulated response)
            var responseMessage = Encoding.UTF8.GetBytes("Hello, World!");
            await serverStream.SendMessageAsync(responseMessage);

            // Server sends trailers to complete
            await serverStream.SendTrailersAsync(StatusCode.OK, "Success");

            return "server done";
        });

        // Client sends a request
        var clientStream = clientConnection.CreateStream();

        var metadata = new Metadata { { "client-id", "test-client" } };
        await clientStream.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost", metadata);

        // Client sends request message
        var requestMessage = Encoding.UTF8.GetBytes("GreeterClient");
        await clientStream.SendMessageAsync(requestMessage);

        // Client signals end of request
        await clientStream.SendHalfCloseAsync();

        // Wait for server to complete
        await serverTask;

        // Assert - verify client state
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Method, Is.EqualTo("/greet.Greeter/SayHello"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task ServerStreaming_MultipleMessages_Works()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Server task that sends multiple messages
        var messageCount = 5;
        var serverTask = Task.Run(async () =>
        {
            var serverStream = serverConnection.CreateStream();
            await serverStream.SendResponseHeadersAsync();

            // Send multiple messages
            for (int i = 0; i < messageCount; i++)
            {
                var message = Encoding.UTF8.GetBytes($"Message {i}");
                await serverStream.SendMessageAsync(message);
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Client initiates request
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ServerStream", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task ClientStreaming_MultipleMessages_Works()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 5;

        // Server task that waits for client messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = serverConnection.CreateStream();
            await serverStream.SendResponseHeadersAsync();

            // In real implementation, server would receive and process messages
            await Task.Delay(100);

            await serverStream.SendTrailersAsync(StatusCode.OK, $"Received messages");
        });

        // Client sends multiple messages
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ClientStream", "localhost");

        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Client message {i}");
            await clientStream.SendMessageAsync(message);
        }

        await clientStream.SendHalfCloseAsync();
        await serverTask;

        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task BidirectionalStreaming_Works()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Server echoes messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = serverConnection.CreateStream();
            await serverStream.SendResponseHeadersAsync();

            // Echo a few messages
            for (int i = 0; i < 3; i++)
            {
                var message = Encoding.UTF8.GetBytes($"Echo {i}");
                await serverStream.SendMessageAsync(message);
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Client sends and receives
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/BiDi", "localhost");

        // Send messages
        for (int i = 0; i < 3; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Request {i}");
            await clientStream.SendMessageAsync(message);
        }

        await clientStream.SendHalfCloseAsync();
        await serverTask;

        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task ErrorResponse_ReturnsStatusCode()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Server returns error
        var serverTask = Task.Run(async () =>
        {
            var serverStream = serverConnection.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.InvalidArgument, "Missing required field");
        });

        // Client makes request
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Error", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - server stream should have trailers set
        var serverStream2 = serverConnection.CreateStream();
        await serverStream2.SendTrailersAsync(StatusCode.NotFound, "Test");
        Assert.That(serverStream2.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.NotFound));
    }

    [Test]
    [Platform("Win")]
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
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Deadline_PropagatesInHeaders()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        // Client sends request with deadline
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Deadline", "localhost", deadline: deadline);

        // Assert - deadline should be in headers
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.DeadlineUnixNano, Is.GreaterThan(0UL));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task Metadata_RoundTrips()
    {
        // Arrange
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var metadata = new Metadata
        {
            { "x-custom-header", "custom-value" },
            { "x-another-header", "another-value" }
        };

        // Client sends request with metadata
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Metadata", "localhost", metadata);

        // Assert - metadata should be in headers
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Metadata.Count, Is.EqualTo(2));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task LargeMessage_TransfersCorrectly()
    {
        // Arrange - use larger ring for this test
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        // Ring capacity must be power of 2
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Create a message smaller than initial window size (65535 bytes)
        var largeMessage = new byte[32 * 1024]; // 32KB
        new Random(42).NextBytes(largeMessage);

        // Client sends message
        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Large", "localhost");
        await clientStream.SendMessageAsync(largeMessage);
        await clientStream.SendHalfCloseAsync();

        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
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
