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

using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmConnectionTests
{
    [Test]
    [Platform("Win")]
    public void ShmConnection_CreateAsServer_CreatesConnection()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";

        // Act
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);

        // Assert
        Assert.That(connection, Is.Not.Null);
        Assert.That(connection.Name, Is.EqualTo(name));
        Assert.That(connection.IsClient, Is.False);
        Assert.That(connection.IsClosed, Is.False);
    }

    [Test]
    [Platform("Win")]
    public void ShmConnection_CreateStream_ReturnsNewStream()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        var stream1 = connection.CreateStream();
        var stream2 = connection.CreateStream();

        // Assert
        Assert.That(stream1, Is.Not.Null);
        Assert.That(stream2, Is.Not.Null);
        // Server uses even stream IDs (2, 4, 6...)
        Assert.That(stream1.StreamId, Is.EqualTo(2));
        Assert.That(stream2.StreamId, Is.EqualTo(4));
    }

    [Test]
    [Platform("Win")]
    public void ShmConnection_ClientConnection_UsesOddStreamIds()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(name);

        // Act
        var stream1 = clientConnection.CreateStream();
        var stream2 = clientConnection.CreateStream();

        // Assert
        Assert.That(clientConnection.IsClient, Is.True);
        // Client uses odd stream IDs (1, 3, 5...)
        Assert.That(stream1.StreamId, Is.EqualTo(1));
        Assert.That(stream2.StreamId, Is.EqualTo(3));
    }

    [Test]
    [Platform("Win")]
    public void ShmConnection_SendGoAway_SetsClosedFlag()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        connection.SendGoAway("shutting down");

        // Assert
        Assert.That(connection.IsClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void ShmConnection_Dispose_ClosesConnection()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        var stream = connection.CreateStream();

        // Act
        connection.Dispose();

        // Assert - should not throw
        Assert.Throws<ObjectDisposedException>(() => connection.CreateStream());
    }

    [Test]
    [Platform("Win")]
    public async Task ShmConnection_DisposeAsync_ClosesConnection()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        await connection.DisposeAsync();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => connection.CreateStream());
    }
}
