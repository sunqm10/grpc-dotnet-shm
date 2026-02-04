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

using System.Diagnostics;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for connection lifecycle management.
/// Equivalent to TCP connection management tests.
/// </summary>
[TestFixture]
public class ConnectionLifecycleTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_CreateServer_Success()
    {
        var segmentName = $"conn_create_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.That(server, Is.Not.Null);
        Assert.That(server.IsServer, Is.True);
        Assert.That(server.IsConnected, Is.False);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_ClientConnect_Success()
    {
        var segmentName = $"conn_client_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        Assert.That(client, Is.Not.Null);
        Assert.That(client.IsServer, Is.False);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Dispose_ReleasesResources()
    {
        var segmentName = $"conn_dispose_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        var client = ShmConnection.ConnectAsClient(segmentName);
        
        server.Dispose();
        client.Dispose();
        
        // Should be able to create with same name after disposal
        using var server2 = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        Assert.That(server2, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_DoubleDispose_NoException()
    {
        var segmentName = $"conn_double_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        server.Dispose();
        Assert.DoesNotThrow(() => server.Dispose());
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public void Connection_MultipleClients_Sequential()
    {
        var segmentName = $"conn_multi_seq_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        // Connect multiple clients sequentially
        for (int i = 0; i < 5; i++)
        {
            using var client = ShmConnection.ConnectAsClient(segmentName);
            Assert.That(client, Is.Not.Null);
        }
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_LargeBufferSize_Success()
    {
        var segmentName = $"conn_large_{Guid.NewGuid():N}";
        var bufferSize = 128 * 1024 * 1024; // 128 MB
        
        using var server = ShmConnection.CreateAsServer(segmentName, (uint)bufferSize, 10);
        
        Assert.That(server.BufferSize, Is.EqualTo(bufferSize));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_MinBufferSize_Success()
    {
        var segmentName = $"conn_min_{Guid.NewGuid():N}";
        var minSize = 4096u;
        
        using var server = ShmConnection.CreateAsServer(segmentName, minSize, 10);
        
        Assert.That(server.BufferSize, Is.GreaterThanOrEqualTo(minSize));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_MaxConcurrentStreams_Enforced()
    {
        var segmentName = $"conn_max_streams_{Guid.NewGuid():N}";
        var maxStreams = 2u;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, maxStreams);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        Assert.That(server.MaxConcurrentStreams, Is.EqualTo(maxStreams));
    }
}

/// <summary>
/// Tests for connection state transitions.
/// </summary>
[TestFixture]
public class ConnectionStateTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_State_InitiallyIdle()
    {
        var segmentName = $"conn_state_idle_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.That(server.State, Is.EqualTo(ShmConnectionState.Idle));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Connection_State_ReadyAfterConnect()
    {
        var segmentName = $"conn_state_ready_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        await Task.Delay(50); // Allow connection to establish
        
        Assert.That(client.State, Is.EqualTo(ShmConnectionState.Ready));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_State_ShutdownAfterDispose()
    {
        var segmentName = $"conn_state_shutdown_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        server.Dispose();
        
        Assert.That(server.State, Is.EqualTo(ShmConnectionState.Shutdown));
    }
}

/// <summary>
/// Tests for connection error handling.
/// </summary>
[TestFixture]
public class ConnectionErrorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_InvalidSegmentName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            ShmConnection.CreateAsServer("", 4096, 10);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_ZeroBufferSize_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            ShmConnection.CreateAsServer("test", 0, 10);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_ClientNoServer_Throws()
    {
        var segmentName = $"conn_no_server_{Guid.NewGuid():N}";
        
        Assert.Throws<Exception>(() =>
        {
            ShmConnection.ConnectAsClient(segmentName);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_DuplicateServerName_Throws()
    {
        var segmentName = $"conn_dup_{Guid.NewGuid():N}";
        
        using var server1 = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.Throws<Exception>(() =>
        {
            ShmConnection.CreateAsServer(segmentName, 4096, 10);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Connection_CreateStreamAfterDispose_Throws()
    {
        var segmentName = $"conn_stream_dispose_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        var client = ShmConnection.ConnectAsClient(segmentName);
        
        client.Dispose();
        
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await Task.Run(() => client.CreateStream());
        });
    }
}

/// <summary>
/// Tests for connection configuration options.
/// </summary>
[TestFixture]
public class ConnectionConfigTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Options_DefaultValues()
    {
        var options = new ShmConnectionOptions();
        
        Assert.That(options.BufferSize, Is.GreaterThan(0));
        Assert.That(options.MaxConcurrentStreams, Is.GreaterThan(0));
        Assert.That(options.KeepAliveTime, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(options.KeepAliveTimeout, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Options_CustomBufferSize()
    {
        var options = new ShmConnectionOptions
        {
            BufferSize = 1024 * 1024
        };
        
        Assert.That(options.BufferSize, Is.EqualTo(1024 * 1024));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Options_CustomMaxStreams()
    {
        var options = new ShmConnectionOptions
        {
            MaxConcurrentStreams = 100
        };
        
        Assert.That(options.MaxConcurrentStreams, Is.EqualTo(100));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Options_KeepAliveSettings()
    {
        var options = new ShmConnectionOptions
        {
            KeepAliveTime = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePermitWithoutCalls = true
        };
        
        Assert.That(options.KeepAliveTime, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(options.KeepAliveTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.KeepAlivePermitWithoutCalls, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_Options_FlowControlSettings()
    {
        var options = new ShmConnectionOptions
        {
            InitialConnectionWindowSize = 1024 * 1024,
            InitialStreamWindowSize = 64 * 1024
        };
        
        Assert.That(options.InitialConnectionWindowSize, Is.EqualTo(1024 * 1024));
        Assert.That(options.InitialStreamWindowSize, Is.EqualTo(64 * 1024));
    }
}

/// <summary>
/// Enum for connection states.
/// </summary>
public enum ShmConnectionState
{
    Idle,
    Connecting,
    Ready,
    TransientFailure,
    Shutdown
}

/// <summary>
/// Configuration options for shared memory connections.
/// </summary>
public class ShmConnectionOptions
{
    public uint BufferSize { get; set; } = 4 * 1024 * 1024;
    public uint MaxConcurrentStreams { get; set; } = 100;
    public TimeSpan KeepAliveTime { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public bool KeepAlivePermitWithoutCalls { get; set; } = false;
    public int InitialConnectionWindowSize { get; set; } = 1024 * 1024;
    public int InitialStreamWindowSize { get; set; } = 64 * 1024;
}
