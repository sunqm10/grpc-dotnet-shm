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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Keepalive parameters for shared memory connections.
/// Mirrors the gRPC keepalive configuration from HTTP/2 transport.
/// </summary>
public sealed class ShmKeepaliveOptions
{
    /// <summary>
    /// Gets or sets the time between keepalive pings.
    /// Default is Infinite (no keepalive pings).
    /// </summary>
    public TimeSpan Time { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets or sets the timeout for keepalive ping acknowledgment.
    /// If the peer doesn't respond within this time, the connection is closed.
    /// Default is 20 seconds.
    /// </summary>
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets whether to send keepalive pings even when there are no active streams.
    /// Default is false.
    /// </summary>
    public bool PermitWithoutStream { get; set; }

    /// <summary>
    /// Gets whether keepalive is enabled.
    /// </summary>
    public bool IsEnabled => Time != System.Threading.Timeout.InfiniteTimeSpan && Time > TimeSpan.Zero;

    /// <summary>
    /// Creates default keepalive options (disabled).
    /// </summary>
    public static ShmKeepaliveOptions Default => new();

    /// <summary>
    /// Creates keepalive options with typical values for active monitoring.
    /// </summary>
    /// <param name="time">Time between pings. Default: 30 seconds.</param>
    /// <param name="pingTimeout">Timeout for ping ack. Default: 20 seconds.</param>
    /// <returns>Configured keepalive options.</returns>
    public static ShmKeepaliveOptions Active(TimeSpan? time = null, TimeSpan? pingTimeout = null)
    {
        return new ShmKeepaliveOptions
        {
            Time = time ?? TimeSpan.FromSeconds(30),
            PingTimeout = pingTimeout ?? TimeSpan.FromSeconds(20),
            PermitWithoutStream = false
        };
    }
}

/// <summary>
/// Server-side keepalive enforcement policy.
/// Used to detect and handle misbehaving clients.
/// </summary>
public sealed class ShmKeepaliveEnforcementPolicy
{
    /// <summary>
    /// Gets or sets the minimum time between pings from the client.
    /// If the client sends pings more frequently, they will be counted as strikes.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan MinTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether to allow pings when there are no active streams.
    /// Default is false.
    /// </summary>
    public bool PermitWithoutStream { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of ping strikes before closing the connection.
    /// Default is 2.
    /// </summary>
    public int MaxPingStrikes { get; set; } = 2;

    /// <summary>
    /// Creates default enforcement policy.
    /// </summary>
    public static ShmKeepaliveEnforcementPolicy Default => new();
}
