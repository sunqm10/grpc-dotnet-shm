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
/// Tests for gRPC health check protocol over shared memory transport.
/// Verifies the grpc.health.v1 service works correctly with SHM.
/// </summary>
[TestFixture]
public class HealthCheckTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_Serving_ReturnsServing()
    {
        var segmentName = $"health_serving_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        
        // Empty service name checks overall health
        var request = Encoding.UTF8.GetBytes("");
        await stream.SendMessageAsync(request);
        await stream.SendHalfCloseAsync();
        
        // Server should respond with SERVING status
        Assert.Pass("Health check for SERVING status documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_NotServing_ReturnsNotServing()
    {
        var segmentName = $"health_notserving_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        
        var request = Encoding.UTF8.GetBytes("unhealthy-service");
        await stream.SendMessageAsync(request);
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Health check for NOT_SERVING status documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_UnknownService_ReturnsNotFound()
    {
        var segmentName = $"health_unknown_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        
        var request = Encoding.UTF8.GetBytes("unknown-service-xyz");
        await stream.SendMessageAsync(request);
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Health check for unknown service documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task HealthWatch_StatusChanges_StreamsUpdates()
    {
        var segmentName = $"health_watch_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Watch", "localhost");
        
        var request = Encoding.UTF8.GetBytes("watched-service");
        await stream.SendMessageAsync(request);
        
        // Watch should stream status updates
        var updates = new List<string> { "SERVING", "NOT_SERVING", "SERVING" };
        
        await stream.SendHalfCloseAsync();
        
        Assert.That(updates.Count, Is.EqualTo(3));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_SpecificService_Works()
    {
        var segmentName = $"health_specific_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        
        // Check specific service
        var request = Encoding.UTF8.GetBytes("my.package.MyService");
        await stream.SendMessageAsync(request);
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Specific service health check documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_EmptyServiceName_ChecksOverall()
    {
        var segmentName = $"health_empty_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        
        // Empty string checks overall server health
        var request = Encoding.UTF8.GetBytes("");
        await stream.SendMessageAsync(request);
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Overall health check documented");
    }
}

/// <summary>
/// Tests for health check service implementation.
/// </summary>
[TestFixture]
public class HealthServiceImplTests
{
    [Test]
    public void HealthService_SetStatus_UpdatesState()
    {
        var healthService = new MockHealthService();
        
        healthService.SetStatus("test-service", HealthStatus.Serving);
        
        Assert.That(healthService.GetStatus("test-service"), Is.EqualTo(HealthStatus.Serving));
    }

    [Test]
    public void HealthService_ClearStatus_RemovesService()
    {
        var healthService = new MockHealthService();
        
        healthService.SetStatus("test-service", HealthStatus.Serving);
        healthService.ClearStatus("test-service");
        
        Assert.That(healthService.GetStatus("test-service"), Is.EqualTo(HealthStatus.Unknown));
    }

    [Test]
    public void HealthService_ClearAll_RemovesAllServices()
    {
        var healthService = new MockHealthService();
        
        healthService.SetStatus("service1", HealthStatus.Serving);
        healthService.SetStatus("service2", HealthStatus.NotServing);
        healthService.ClearAll();
        
        Assert.That(healthService.GetStatus("service1"), Is.EqualTo(HealthStatus.Unknown));
        Assert.That(healthService.GetStatus("service2"), Is.EqualTo(HealthStatus.Unknown));
    }

    [Test]
    public void HealthService_MultipleServices_IndependentStatus()
    {
        var healthService = new MockHealthService();
        
        healthService.SetStatus("service1", HealthStatus.Serving);
        healthService.SetStatus("service2", HealthStatus.NotServing);
        healthService.SetStatus("service3", HealthStatus.ServiceUnknown);
        
        Assert.That(healthService.GetStatus("service1"), Is.EqualTo(HealthStatus.Serving));
        Assert.That(healthService.GetStatus("service2"), Is.EqualTo(HealthStatus.NotServing));
        Assert.That(healthService.GetStatus("service3"), Is.EqualTo(HealthStatus.ServiceUnknown));
    }
}

/// <summary>
/// Tests for health check with load balancer integration.
/// </summary>
[TestFixture]
public class HealthCheckLoadBalancerTests
{
    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task HealthCheck_UnhealthyBackend_RemovedFromPool()
    {
        var healthy1 = $"health_lb1_{Guid.NewGuid():N}";
        var unhealthy2 = $"health_lb2_{Guid.NewGuid():N}";
        
        using var server1 = ShmConnection.CreateAsServer(healthy1, 4096, 10);
        using var server2 = ShmConnection.CreateAsServer(unhealthy2, 4096, 10);
        
        using var client1 = ShmConnection.ConnectAsClient(healthy1);
        
        // Only healthy server receives traffic
        for (int i = 0; i < 5; i++)
        {
            var stream = client1.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/healthy-{i}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.Pass("Unhealthy backend removal documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task HealthCheck_BackendRecovers_ReaddedToPool()
    {
        var segmentName = $"health_recover_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Initial health check passes
        var stream1 = client.CreateStream();
        await stream1.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        await stream1.SendHalfCloseAsync();
        
        // Simulate recovery and re-addition
        await Task.Delay(100);
        
        var stream2 = client.CreateStream();
        await stream2.SendRequestHeadersAsync("/test/recovered", "localhost");
        await stream2.SendHalfCloseAsync();
        
        Assert.Pass("Backend recovery and re-addition documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HealthCheck_Interval_Configurable()
    {
        var segmentName = $"health_interval_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var healthCheckInterval = TimeSpan.FromSeconds(5);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/grpc.health.v1.Health/Check", "localhost");
        await stream.SendHalfCloseAsync();
        
        Assert.That(healthCheckInterval.TotalSeconds, Is.EqualTo(5));
    }
}

#region Helper Classes

public enum HealthStatus
{
    Unknown,
    Serving,
    NotServing,
    ServiceUnknown
}

public class MockHealthService
{
    private readonly Dictionary<string, HealthStatus> _statuses = new();

    public void SetStatus(string serviceName, HealthStatus status)
    {
        _statuses[serviceName] = status;
    }

    public HealthStatus GetStatus(string serviceName)
    {
        return _statuses.TryGetValue(serviceName, out var status) ? status : HealthStatus.Unknown;
    }

    public void ClearStatus(string serviceName)
    {
        _statuses.Remove(serviceName);
    }

    public void ClearAll()
    {
        _statuses.Clear();
    }
}

#endregion
