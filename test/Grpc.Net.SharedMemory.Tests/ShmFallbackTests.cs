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

using Grpc.Net.SharedMemory;
using Grpc.Net.SharedMemory.LoadBalancing;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for <see cref="ShmFallbackHandler"/> and <see cref="ShmServicePolicy"/>.
/// </summary>
[TestFixture]
public class ShmFallbackTests
{
    // ============================================================
    // ShmServicePolicy tests
    // ============================================================

    [Test]
    public void ShmServicePolicy_Defaults_AreCorrect()
    {
        var policy = new ShmServicePolicy();
        Assert.That(policy.Policy, Is.EqualTo(ShmServicePolicy.Auto));
        Assert.That(policy.AllowFallback, Is.True);
    }

    [Test]
    public void ShmServicePolicy_Validate_ThrowsOnInvalid()
    {
        var policy = new ShmServicePolicy { Policy = "bogus" };
        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [TestCase(ShmServicePolicy.Auto, true, true)]
    [TestCase(ShmServicePolicy.Auto, false, false)]
    [TestCase("preferred", true, true)]
    [TestCase("preferred", false, false)]
    [TestCase("required", true, true)]
    [TestCase("required", false, false)]
    [TestCase("disabled", true, false)]
    [TestCase("disabled", false, false)]
    public void ShmServicePolicy_ShouldUseShm_CorrectForPolicies(string policy, bool hasCapability, bool expected)
    {
        var sp = new ShmServicePolicy { Policy = policy };
        Assert.That(sp.ShouldUseShm(hasCapability), Is.EqualTo(expected));
    }

    [TestCase(ShmServicePolicy.Auto)]
    [TestCase("preferred")]
    [TestCase("required")]
    [TestCase("disabled")]
    public void ShmServicePolicy_Validate_AllValidPoliciesPass(string policy)
    {
        var sp = new ShmServicePolicy { Policy = policy };
        Assert.DoesNotThrow(() => sp.Validate());
    }

    // ============================================================
    // ShmFallbackHandler construction tests
    // ============================================================

    [Test]
    public void ShmFallbackHandler_Constructor_ThrowsOnNullSegmentName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ShmFallbackHandler(null!, "https://localhost:5001"));
    }

    [Test]
    public void ShmFallbackHandler_Constructor_ThrowsOnNullTcpAddress()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ShmFallbackHandler("segment", null!));
    }

    [Test]
    public void ShmFallbackHandler_Constructor_ThrowsOnInvalidPolicy()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ShmFallbackHandler("segment", "https://localhost:5001",
                new ShmServicePolicy { Policy = "bogus" }));
    }

    [Test]
    public void ShmFallbackHandler_Properties_AreSet()
    {
        using var handler = new ShmFallbackHandler("my_segment", "https://localhost:5001");
        Assert.That(handler.SegmentName, Is.EqualTo("my_segment"));
        Assert.That(handler.TcpFallbackAddress, Is.EqualTo("https://localhost:5001"));
        Assert.That(handler.IsFallenBack, Is.False);
        Assert.That(handler.ShmAttempts, Is.EqualTo(0));
        Assert.That(handler.ShmSuccesses, Is.EqualTo(0));
        Assert.That(handler.TcpFallbacks, Is.EqualTo(0));
    }

    // ============================================================
    // ShmFallbackHandler policy: disabled → always TCP
    // ============================================================

    [Test]
    public async Task ShmFallbackHandler_DisabledPolicy_SendsViaTcp()
    {
        var tcpHandler = new FakeOkHandler();
        using var handler = new ShmFallbackHandler(
            "nonexistent_segment",
            "https://localhost:5001",
            new ShmServicePolicy { Policy = ShmServicePolicy.Disabled },
            tcpHandler: tcpHandler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method");
        var invoker = new HttpMessageInvoker(handler);
        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(tcpHandler.Calls, Is.EqualTo(1));
        Assert.That(handler.TcpFallbacks, Is.EqualTo(1));
        Assert.That(handler.ShmAttempts, Is.EqualTo(0));
    }

    // ============================================================
    // ShmFallbackHandler policy: required + SHM fails → no fallback
    // ============================================================

    [Test]
    public void ShmFallbackHandler_RequiredPolicy_ShmFails_Throws()
    {
        // Segment doesn't exist → SHM will fail to connect
        var tcpHandler = new FakeOkHandler();
        using var handler = new ShmFallbackHandler(
            "nonexistent_fallback_test_segment",
            "https://localhost:5001",
            new ShmServicePolicy { Policy = ShmServicePolicy.Required },
            tcpHandler: tcpHandler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method");
        var invoker = new HttpMessageInvoker(handler);

        // Required policy should NOT fall back — should throw
        Assert.That(async () =>
            await invoker.SendAsync(request, CancellationToken.None),
            Throws.InstanceOf<Exception>());

        // TCP should not have been called
        Assert.That(tcpHandler.Calls, Is.EqualTo(0));
        Assert.That(handler.ShmAttempts, Is.EqualTo(1));
        Assert.That(handler.TcpFallbacks, Is.EqualTo(0));
    }

    // ============================================================
    // ShmFallbackHandler: auto policy + SHM fails → falls back to TCP
    // ============================================================

    [Test]
    public async Task ShmFallbackHandler_AutoPolicy_ShmFails_FallsBackToTcp()
    {
        var tcpHandler = new FakeOkHandler();
        using var handler = new ShmFallbackHandler(
            "nonexistent_fallback_test_segment",
            "https://localhost:5001",
            new ShmServicePolicy { Policy = ShmServicePolicy.Auto, AllowFallback = true },
            tcpHandler: tcpHandler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method");
        var invoker = new HttpMessageInvoker(handler);
        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(handler.IsFallenBack, Is.True);
        Assert.That(handler.ShmAttempts, Is.EqualTo(1));
        Assert.That(handler.TcpFallbacks, Is.EqualTo(1));
        Assert.That(tcpHandler.Calls, Is.EqualTo(1));
    }

    // ============================================================
    // ShmFallbackHandler: second call after fallback → fast path to TCP
    // ============================================================

    [Test]
    public async Task ShmFallbackHandler_AfterFallback_SubsequentCallsGoDirectlyToTcp()
    {
        var tcpHandler = new FakeOkHandler();
        using var handler = new ShmFallbackHandler(
            "nonexistent_fallback_test_segment",
            "https://localhost:5001",
            tcpHandler: tcpHandler);

        var invoker = new HttpMessageInvoker(handler);

        // First call: tries SHM, falls back to TCP
        var req1 = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method1");
        await invoker.SendAsync(req1, CancellationToken.None);
        Assert.That(handler.IsFallenBack, Is.True);
        Assert.That(handler.ShmAttempts, Is.EqualTo(1));

        // Second call: should go directly to TCP (fast path)
        var req2 = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method2");
        await invoker.SendAsync(req2, CancellationToken.None);
        Assert.That(handler.ShmAttempts, Is.EqualTo(1)); // no new SHM attempt
        Assert.That(handler.TcpFallbacks, Is.EqualTo(2));
        Assert.That(tcpHandler.Calls, Is.EqualTo(2));
    }

    // ============================================================
    // ShmFallbackHandler: AllowFallback=false + SHM fails → throws
    // ============================================================

    [Test]
    public void ShmFallbackHandler_FallbackDisabled_ShmFails_Throws()
    {
        var tcpHandler = new FakeOkHandler();
        using var handler = new ShmFallbackHandler(
            "nonexistent_fallback_test_segment",
            "https://localhost:5001",
            new ShmServicePolicy { Policy = ShmServicePolicy.Auto, AllowFallback = false },
            tcpHandler: tcpHandler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method");
        var invoker = new HttpMessageInvoker(handler);

        Assert.That(async () =>
            await invoker.SendAsync(request, CancellationToken.None),
            Throws.InstanceOf<Exception>());
        Assert.That(tcpHandler.Calls, Is.EqualTo(0));
    }

    // ============================================================
    // ShmFallbackHandler: dispose cleans up both handlers
    // ============================================================

    [Test]
    public void ShmFallbackHandler_Dispose_CleansUpBothHandlers()
    {
        var tcpHandler = new FakeOkHandler();
        var handler = new ShmFallbackHandler(
            "segment",
            "https://localhost:5001",
            tcpHandler: tcpHandler);

        handler.Dispose();

        Assert.That(tcpHandler.IsDisposed, Is.True);
    }

    [Test]
    public void ShmFallbackHandler_AfterDispose_ThrowsObjectDisposed()
    {
        var handler = new ShmFallbackHandler("segment", "https://localhost:5001");
        handler.Dispose();

        var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/service/Method");
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await invoker.SendAsync(request, CancellationToken.None));
    }

    // ============================================================
    // Helper: Fake HTTP handler that returns 200 OK
    // ============================================================

    private class FakeOkHandler : HttpMessageHandler
    {
        public int Calls;
        public bool IsDisposed;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
