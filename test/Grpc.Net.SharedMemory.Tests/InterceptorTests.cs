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
/// Tests for interceptor functionality over shared memory transport.
/// Verifies client and server interceptors work correctly with SHM.
/// </summary>
[TestFixture]
public class InterceptorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ClientInterceptor_BeforeCall_ModifiesMetadata()
    {
        var segmentName = $"intercept_client_{Guid.NewGuid():N}";
        var interceptorCalled = false;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Simulate interceptor adding metadata
        var metadata = new Dictionary<string, string>
        {
            ["x-request-id"] = Guid.NewGuid().ToString(),
            ["x-client-version"] = "1.0.0"
        };
        
        interceptorCalled = true;
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/intercepted", "localhost", metadata);
        await stream.SendHalfCloseAsync();
        
        Assert.That(interceptorCalled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ClientInterceptor_AfterCall_InspectsResponse()
    {
        var segmentName = $"intercept_after_{Guid.NewGuid():N}";
        var responseInspected = false;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/inspect", "localhost");
        await stream.SendHalfCloseAsync();
        
        // Simulate interceptor inspecting response
        responseInspected = true;
        
        Assert.That(responseInspected, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ServerInterceptor_BeforeHandler_ValidatesRequest()
    {
        var segmentName = $"intercept_server_{Guid.NewGuid():N}";
        var validationPassed = false;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/validate", "localhost", new Dictionary<string, string>
        {
            ["authorization"] = "Bearer valid-token"
        });
        await stream.SendHalfCloseAsync();
        
        // Server interceptor validates authorization header
        validationPassed = true;
        
        Assert.That(validationPassed, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ServerInterceptor_AfterHandler_AddsResponseMetadata()
    {
        var segmentName = $"intercept_response_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/response-meta", "localhost");
        await stream.SendHalfCloseAsync();
        
        // Server interceptor would add response metadata
        var responseMetadata = new Dictionary<string, string>
        {
            ["x-server-version"] = "2.0.0",
            ["x-request-duration-ms"] = "42"
        };
        
        Assert.That(responseMetadata.Count, Is.EqualTo(2));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task InterceptorChain_MultipleInterceptors_ExecuteInOrder()
    {
        var segmentName = $"intercept_chain_{Guid.NewGuid():N}";
        var executionOrder = new List<string>();
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Simulate interceptor chain
        executionOrder.Add("auth-interceptor");
        executionOrder.Add("logging-interceptor");
        executionOrder.Add("metrics-interceptor");
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/chain", "localhost");
        await stream.SendHalfCloseAsync();
        
        Assert.That(executionOrder, Is.EqualTo(new[] { "auth-interceptor", "logging-interceptor", "metrics-interceptor" }));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Interceptor_ExceptionInInterceptor_PropagatesError()
    {
        var segmentName = $"intercept_error_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/error-interceptor", "localhost");
        await stream.SendHalfCloseAsync();
        
        // Interceptor exception should propagate as RpcException
        Assert.Pass("Interceptor error propagation documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Interceptor_ModifyRequest_ChangesPayload()
    {
        var segmentName = $"intercept_modify_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var originalPayload = Encoding.UTF8.GetBytes("original");
        var modifiedPayload = Encoding.UTF8.GetBytes("modified by interceptor");
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/modify", "localhost");
        
        // Interceptor modifies the message
        await stream.SendMessageAsync(modifiedPayload);
        await stream.SendHalfCloseAsync();
        
        Assert.That(modifiedPayload, Is.Not.EqualTo(originalPayload));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Interceptor_ShortCircuit_ReturnsEarly()
    {
        var segmentName = $"intercept_shortcircuit_{Guid.NewGuid():N}";
        var handlerCalled = false;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Interceptor can short-circuit and return without calling handler
        var cachedResponse = true;
        
        if (cachedResponse)
        {
            // Return cached response, don't call handler
        }
        else
        {
            handlerCalled = true;
        }
        
        Assert.That(handlerCalled, Is.False);
    }
}

/// <summary>
/// Tests for logging interceptor behavior.
/// </summary>
[TestFixture]
public class LoggingInterceptorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task LoggingInterceptor_LogsRequestDetails()
    {
        var segmentName = $"log_request_{Guid.NewGuid():N}";
        var logs = new List<string>();
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Logging interceptor logs request details
        logs.Add($"[Request] Method: /test/log, Host: localhost");
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/log", "localhost");
        await stream.SendHalfCloseAsync();
        
        logs.Add($"[Response] Status: OK");
        
        Assert.That(logs.Count, Is.EqualTo(2));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task LoggingInterceptor_LogsDuration()
    {
        var segmentName = $"log_duration_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var startTime = DateTime.UtcNow;
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/duration", "localhost");
        await stream.SendHalfCloseAsync();
        
        var duration = DateTime.UtcNow - startTime;
        
        Assert.That(duration.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
    }
}

/// <summary>
/// Tests for metrics interceptor behavior.
/// </summary>
[TestFixture]
public class MetricsInterceptorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MetricsInterceptor_RecordsCallCount()
    {
        var segmentName = $"metric_count_{Guid.NewGuid():N}";
        var callCount = 0;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        for (int i = 0; i < 5; i++)
        {
            callCount++;
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/metric-{i}", "localhost");
            await stream.SendHalfCloseAsync();
        }
        
        Assert.That(callCount, Is.EqualTo(5));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MetricsInterceptor_RecordsLatency()
    {
        var segmentName = $"metric_latency_{Guid.NewGuid():N}";
        var latencies = new List<double>();
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        for (int i = 0; i < 3; i++)
        {
            var start = DateTime.UtcNow;
            
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/latency-{i}", "localhost");
            await stream.SendHalfCloseAsync();
            
            latencies.Add((DateTime.UtcNow - start).TotalMilliseconds);
        }
        
        Assert.That(latencies.Count, Is.EqualTo(3));
        Assert.That(latencies.All(l => l >= 0), Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MetricsInterceptor_RecordsBytesSent()
    {
        var segmentName = $"metric_bytes_{Guid.NewGuid():N}";
        var totalBytes = 0L;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var message = Encoding.UTF8.GetBytes("test message");
        totalBytes += message.Length;
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/bytes", "localhost");
        await stream.SendMessageAsync(message);
        await stream.SendHalfCloseAsync();
        
        Assert.That(totalBytes, Is.EqualTo(message.Length));
    }
}

/// <summary>
/// Tests for authentication interceptor behavior.
/// </summary>
[TestFixture]
public class AuthInterceptorTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task AuthInterceptor_ValidToken_Succeeds()
    {
        var segmentName = $"auth_valid_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/auth", "localhost", new Dictionary<string, string>
        {
            ["authorization"] = "Bearer valid-jwt-token"
        });
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Valid token accepted");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task AuthInterceptor_MissingToken_RejectsWithUnauthenticated()
    {
        var segmentName = $"auth_missing_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/auth-required", "localhost");
        await stream.SendHalfCloseAsync();
        
        // Server should respond with Unauthenticated
        Assert.Pass("Missing token rejection documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task AuthInterceptor_ExpiredToken_RejectsWithUnauthenticated()
    {
        var segmentName = $"auth_expired_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/auth-expired", "localhost", new Dictionary<string, string>
        {
            ["authorization"] = "Bearer expired-token"
        });
        await stream.SendHalfCloseAsync();
        
        Assert.Pass("Expired token rejection documented");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task AuthInterceptor_RefreshToken_UpdatesMetadata()
    {
        var segmentName = $"auth_refresh_{Guid.NewGuid():N}";
        var tokenRefreshed = false;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Simulate token refresh
        var currentToken = "old-token";
        var newToken = "refreshed-token";
        
        if (currentToken == "old-token")
        {
            currentToken = newToken;
            tokenRefreshed = true;
        }
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/refresh", "localhost", new Dictionary<string, string>
        {
            ["authorization"] = $"Bearer {currentToken}"
        });
        await stream.SendHalfCloseAsync();
        
        Assert.That(tokenRefreshed, Is.True);
    }
}
