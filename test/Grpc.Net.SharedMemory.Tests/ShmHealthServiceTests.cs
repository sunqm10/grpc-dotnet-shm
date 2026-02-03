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

using Grpc.Net.SharedMemory.Health;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmHealthServiceTests
{
    [Test]
    public void NewService_DefaultsToServing()
    {
        // Arrange
        var service = new ShmHealthService();

        // Act
        var status = service.Check("");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.Serving));
    }

    [Test]
    public void SetServingStatus_UpdatesStatus()
    {
        // Arrange
        var service = new ShmHealthService();

        // Act
        service.SetServingStatus("test.Service", ShmServingStatus.NotServing);
        var status = service.Check("test.Service");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.NotServing));
    }

    [Test]
    public void Check_ReturnsServiceUnknown_ForUnknownService()
    {
        // Arrange
        var service = new ShmHealthService();

        // Act
        var status = service.Check("unknown.service");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.ServiceUnknown));
    }

    [Test]
    public async Task Watch_ReceivesStatusUpdates()
    {
        // Arrange
        var service = new ShmHealthService();
        var serviceName = "test.WatchService";
        var receivedStatuses = new List<ShmServingStatus>();
        var cts = new CancellationTokenSource();

        service.SetServingStatus(serviceName, ShmServingStatus.Serving);

        // Start watching
        var watchTask = Task.Run(async () =>
        {
            await foreach (var status in service.Watch(serviceName, cts.Token))
            {
                receivedStatuses.Add(status);
                if (receivedStatuses.Count >= 2)
                    break;
            }
        });

        // Act - update the status
        await Task.Delay(50);
        service.SetServingStatus(serviceName, ShmServingStatus.NotServing);
        
        // Wait for watch to receive updates
        await Task.WhenAny(watchTask, Task.Delay(1000));
        cts.Cancel();

        // Assert
        Assert.That(receivedStatuses, Does.Contain(ShmServingStatus.Serving));
        Assert.That(receivedStatuses, Does.Contain(ShmServingStatus.NotServing));
    }

    [Test]
    public void ClearStatus_RemovesServiceStatus()
    {
        // Arrange
        var service = new ShmHealthService();
        service.SetServingStatus("test.Service", ShmServingStatus.Serving);

        // Act
        service.ClearStatus("test.Service");
        var status = service.Check("test.Service");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.ServiceUnknown));
    }

    [Test]
    public void ClearAll_RemovesAllStatuses()
    {
        // Arrange
        var service = new ShmHealthService();
        service.SetServingStatus("service1", ShmServingStatus.Serving);
        service.SetServingStatus("service2", ShmServingStatus.NotServing);

        // Act
        service.ClearAll();

        // Assert
        Assert.That(service.Check("service1"), Is.EqualTo(ShmServingStatus.ServiceUnknown));
        Assert.That(service.Check("service2"), Is.EqualTo(ShmServingStatus.ServiceUnknown));
    }

    [Test]
    public void Shutdown_SetsAllServicesToNotServing()
    {
        // Arrange
        var service = new ShmHealthService();
        service.SetServingStatus("service1", ShmServingStatus.Serving);
        service.SetServingStatus("service2", ShmServingStatus.Serving);

        // Act
        service.Shutdown();

        // Assert
        Assert.That(service.Check("service1"), Is.EqualTo(ShmServingStatus.NotServing));
        Assert.That(service.Check("service2"), Is.EqualTo(ShmServingStatus.NotServing));
    }

    [Test]
    public void GetAllStatuses_ReturnsAllRegisteredServices()
    {
        // Arrange
        var service = new ShmHealthService();
        service.SetServingStatus("service1", ShmServingStatus.Serving);
        service.SetServingStatus("service2", ShmServingStatus.NotServing);

        // Act
        var statuses = service.GetAllStatuses();

        // Assert
        Assert.That(statuses.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(statuses["service1"], Is.EqualTo(ShmServingStatus.Serving));
        Assert.That(statuses["service2"], Is.EqualTo(ShmServingStatus.NotServing));
    }
}

[TestFixture]
public class ShmHealthCheckStateTests
{
    [Test]
    public void NewState_StartsHealthy()
    {
        // Arrange
        var options = new ShmHealthCheckOptions { Enabled = true };
        
        // Act
        var state = new ShmHealthCheckState(options);

        // Assert
        Assert.That(state.IsHealthy, Is.True);
    }

    [Test]
    public void RecordFailure_DecreasesConsecutiveSuccesses()
    {
        // Arrange
        var options = new ShmHealthCheckOptions { Enabled = true };
        var state = new ShmHealthCheckState(options);
        state.RecordSuccess();
        state.RecordSuccess();

        // Act
        state.RecordFailure();

        // Assert - should still be healthy after one failure
        Assert.That(state.IsHealthy, Is.True);
    }

    [Test]
    public void MultipleFailures_MarksUnhealthy()
    {
        // Arrange
        var options = new ShmHealthCheckOptions 
        { 
            Enabled = true,
            UnhealthyThreshold = 2
        };
        var state = new ShmHealthCheckState(options);

        // Act
        state.RecordFailure();
        state.RecordFailure();

        // Assert
        Assert.That(state.IsHealthy, Is.False);
    }

    [Test]
    public void RecordSuccess_ResetsFailureCount()
    {
        // Arrange
        var options = new ShmHealthCheckOptions 
        { 
            Enabled = true,
            UnhealthyThreshold = 3
        };
        var state = new ShmHealthCheckState(options);
        state.RecordFailure();
        state.RecordFailure();

        // Act
        state.RecordSuccess();
        state.RecordFailure();

        // Assert - should still be healthy (failure count was reset)
        Assert.That(state.IsHealthy, Is.True);
    }

    [Test]
    public void IsCheckDue_ReturnsTrue_WhenIntervalElapsed()
    {
        // Arrange
        var options = new ShmHealthCheckOptions 
        { 
            Enabled = true,
            Interval = TimeSpan.Zero // Immediate
        };
        var state = new ShmHealthCheckState(options);

        // Assert
        Assert.That(state.IsCheckDue(), Is.True);
    }

    [Test]
    public void Disabled_NeverChecksDue()
    {
        // Arrange
        var options = new ShmHealthCheckOptions 
        { 
            Enabled = false,
            Interval = TimeSpan.Zero
        };
        var state = new ShmHealthCheckState(options);

        // Assert
        Assert.That(state.IsCheckDue(), Is.False);
    }

    [Test]
    public void HealthCheckOptions_DefaultValues()
    {
        // Act
        var options = ShmHealthCheckOptions.Default;

        // Assert
        Assert.That(options.Enabled, Is.True);
        Assert.That(options.Interval, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(options.Timeout, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(options.UnhealthyThreshold, Is.GreaterThan(0));
        Assert.That(options.HealthyThreshold, Is.GreaterThan(0));
    }

    [Test]
    public void HealthCheckOptions_Disabled_HasEnabledFalse()
    {
        // Act
        var options = ShmHealthCheckOptions.Disabled;

        // Assert
        Assert.That(options.Enabled, Is.False);
    }
}
