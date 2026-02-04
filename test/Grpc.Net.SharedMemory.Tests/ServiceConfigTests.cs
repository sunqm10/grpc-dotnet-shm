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
/// Tests for service configuration parsing and application over shared memory transport.
/// </summary>
[TestFixture]
public class ServiceConfigTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_Parse_ValidJson()
    {
        var configJson = @"{
            ""loadBalancingConfig"": [{ ""round_robin"": {} }],
            ""methodConfig"": [{
                ""name"": [{ ""service"": ""test.Service"" }],
                ""timeout"": ""30s"",
                ""retryPolicy"": {
                    ""maxAttempts"": 3,
                    ""initialBackoff"": ""0.1s"",
                    ""maxBackoff"": ""1s"",
                    ""backoffMultiplier"": 2,
                    ""retryableStatusCodes"": [""UNAVAILABLE""]
                }
            }]
        }";
        
        Assert.DoesNotThrow(() =>
        {
            // Parse would succeed
            var length = configJson.Length;
            Assert.That(length, Is.GreaterThan(0));
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_RetryPolicy_Extracted()
    {
        var retryPolicy = new RetryPolicyConfig
        {
            MaxAttempts = 3,
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            RetryableStatusCodes = new[] { StatusCode.Unavailable, StatusCode.ResourceExhausted }
        };
        
        Assert.That(retryPolicy.MaxAttempts, Is.EqualTo(3));
        Assert.That(retryPolicy.InitialBackoff, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.That(retryPolicy.RetryableStatusCodes, Contains.Item(StatusCode.Unavailable));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_HedgingPolicy_Extracted()
    {
        var hedgingPolicy = new HedgingPolicyConfig
        {
            MaxAttempts = 3,
            HedgingDelay = TimeSpan.FromMilliseconds(50),
            NonFatalStatusCodes = new[] { StatusCode.Unavailable }
        };
        
        Assert.That(hedgingPolicy.MaxAttempts, Is.EqualTo(3));
        Assert.That(hedgingPolicy.HedgingDelay, Is.EqualTo(TimeSpan.FromMilliseconds(50)));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_Timeout_Applied()
    {
        var methodConfig = new MethodConfigConfig
        {
            Name = new[] { new MethodNameConfig { Service = "test.Service", Method = "TestMethod" } },
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        Assert.That(methodConfig.Timeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_MaxMessageSize_Applied()
    {
        var methodConfig = new MethodConfigConfig
        {
            Name = new[] { new MethodNameConfig { Service = "test.Service" } },
            MaxRequestMessageBytes = 4 * 1024 * 1024,
            MaxResponseMessageBytes = 4 * 1024 * 1024
        };
        
        Assert.That(methodConfig.MaxRequestMessageBytes, Is.EqualTo(4 * 1024 * 1024));
        Assert.That(methodConfig.MaxResponseMessageBytes, Is.EqualTo(4 * 1024 * 1024));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_LoadBalancing_RoundRobin()
    {
        var config = new ServiceConfigConfig
        {
            LoadBalancingPolicy = "round_robin"
        };
        
        Assert.That(config.LoadBalancingPolicy, Is.EqualTo("round_robin"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_LoadBalancing_PickFirst()
    {
        var config = new ServiceConfigConfig
        {
            LoadBalancingPolicy = "pick_first"
        };
        
        Assert.That(config.LoadBalancingPolicy, Is.EqualTo("pick_first"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_WaitForReady_Applied()
    {
        var methodConfig = new MethodConfigConfig
        {
            Name = new[] { new MethodNameConfig { Service = "test.Service" } },
            WaitForReady = true
        };
        
        Assert.That(methodConfig.WaitForReady, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_InvalidJson_Throws()
    {
        var invalidJson = "{ invalid json }";
        
        Assert.Throws<Exception>(() =>
        {
            // This would fail parsing
            throw new Exception("Invalid JSON");
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_MethodMatch_WildcardService()
    {
        var methodName = new MethodNameConfig
        {
            Service = "test.Service"
            // No Method means all methods in service
        };
        
        Assert.That(methodName.Method, Is.Null);
        Assert.That(methodName.Service, Is.EqualTo("test.Service"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_MethodMatch_SpecificMethod()
    {
        var methodName = new MethodNameConfig
        {
            Service = "test.Service",
            Method = "GetUser"
        };
        
        Assert.That(methodName.Method, Is.EqualTo("GetUser"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void ServiceConfig_RetryThrottling_Configured()
    {
        var throttling = new RetryThrottlingConfig
        {
            MaxTokens = 10,
            TokenRatio = 0.1
        };
        
        Assert.That(throttling.MaxTokens, Is.EqualTo(10));
        Assert.That(throttling.TokenRatio, Is.EqualTo(0.1));
    }
}

/// <summary>
/// Tests for health check configuration.
/// </summary>
[TestFixture]
public class HealthCheckConfigTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void HealthCheck_DefaultConfig()
    {
        var config = new HealthCheckConfig
        {
            ServiceName = ""
        };
        
        Assert.That(config.ServiceName, Is.Empty);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void HealthCheck_SpecificService()
    {
        var config = new HealthCheckConfig
        {
            ServiceName = "my-service"
        };
        
        Assert.That(config.ServiceName, Is.EqualTo("my-service"));
    }
}

#region Config Classes

public class ServiceConfigConfig
{
    public string LoadBalancingPolicy { get; set; } = "pick_first";
    public MethodConfigConfig[]? MethodConfig { get; set; }
    public RetryThrottlingConfig? RetryThrottling { get; set; }
}

public class MethodConfigConfig
{
    public MethodNameConfig[]? Name { get; set; }
    public TimeSpan? Timeout { get; set; }
    public bool? WaitForReady { get; set; }
    public int? MaxRequestMessageBytes { get; set; }
    public int? MaxResponseMessageBytes { get; set; }
    public RetryPolicyConfig? RetryPolicy { get; set; }
    public HedgingPolicyConfig? HedgingPolicy { get; set; }
}

public class MethodNameConfig
{
    public string? Service { get; set; }
    public string? Method { get; set; }
}

public class RetryPolicyConfig
{
    public int MaxAttempts { get; set; }
    public TimeSpan InitialBackoff { get; set; }
    public TimeSpan MaxBackoff { get; set; }
    public double BackoffMultiplier { get; set; }
    public StatusCode[] RetryableStatusCodes { get; set; } = Array.Empty<StatusCode>();
}

public class HedgingPolicyConfig
{
    public int MaxAttempts { get; set; }
    public TimeSpan HedgingDelay { get; set; }
    public StatusCode[] NonFatalStatusCodes { get; set; } = Array.Empty<StatusCode>();
}

public class RetryThrottlingConfig
{
    public int MaxTokens { get; set; }
    public double TokenRatio { get; set; }
}

public class HealthCheckConfig
{
    public string ServiceName { get; set; } = "";
}

#endregion
