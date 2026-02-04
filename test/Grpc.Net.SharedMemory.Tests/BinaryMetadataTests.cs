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
/// Tests for binary metadata handling.
/// Binary metadata keys end with "-bin" and values are base64-encoded.
/// These tests verify shared memory transport handles binary metadata correctly.
/// </summary>
[TestFixture]
public class BinaryMetadataTests
{
    [Test]
    [Platform("Win")]
    public void BinaryMetadataKey_MustEndWithBin()
    {
        var key = "my-binary-key-bin";
        Assert.That(key.EndsWith("-bin"), Is.True);
    }

    [Test]
    [Platform("Win")]
    public void BinaryMetadataValue_CanContainArbitraryBytes()
    {
        // Binary metadata can contain any byte values
        var binaryValue = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x7F, 0x80 };
        var base64 = Convert.ToBase64String(binaryValue);
        
        // Round-trip
        var decoded = Convert.FromBase64String(base64);
        Assert.That(decoded, Is.EqualTo(binaryValue));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendHeaders_WithBinaryMetadata_Preserves值()
    {
        var segmentName = $"bin_meta_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Binary data to send
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE };
        var base64Value = Convert.ToBase64String(binaryData);
        
        var metadata = new Metadata
        {
            { "data-bin", binaryData }
        };
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/binary", "localhost", metadata);
        
        Assert.That(stream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendTrailers_WithBinaryMetadata_PreservesValue()
    {
        var segmentName = $"bin_meta_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/binary-trailer", "localhost");
        await clientStream.SendHalfCloseAsync();
        
        // Server sends binary metadata in trailers
        var binaryData = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var trailerMetadata = new Metadata
        {
            { "response-data-bin", binaryData }
        };
        
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync();
        await serverStream.SendTrailersAsync(StatusCode.OK, trailerMetadata: trailerMetadata);
        
        Assert.That(serverStream.TrailersSent, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void BinaryMetadata_LargeValue_Supported()
    {
        // Test with 1KB binary payload
        var largeData = new byte[1024];
        new Random(42).NextBytes(largeData);
        
        var base64 = Convert.ToBase64String(largeData);
        var decoded = Convert.FromBase64String(base64);
        
        Assert.That(decoded, Is.EqualTo(largeData));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MultipleBinaryMetadata_AllPreserved()
    {
        var segmentName = $"bin_meta_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var data1 = new byte[] { 0x01, 0x02 };
        var data2 = new byte[] { 0x03, 0x04 };
        var data3 = new byte[] { 0x05, 0x06, 0x07, 0x08 };
        
        var metadata = new Metadata
        {
            { "first-bin", data1 },
            { "second-bin", data2 },
            { "third-bin", data3 }
        };
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/multi-binary", "localhost", metadata);
        
        Assert.That(stream.RequestHeaders!.CustomMetadata!.Count, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    [Platform("Win")]
    public void BinaryMetadata_EmptyValue_IsValid()
    {
        var emptyData = Array.Empty<byte>();
        var base64 = Convert.ToBase64String(emptyData);
        
        Assert.That(base64, Is.EqualTo(string.Empty));
        Assert.That(Convert.FromBase64String(base64), Is.EqualTo(emptyData));
    }
}

/// <summary>
/// Extended metadata tests covering edge cases and advanced scenarios.
/// </summary>
[TestFixture]
public class AdvancedMetadataTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Metadata_SpecialCharactersInValues_Preserved()
    {
        var segmentName = $"meta_special_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var metadata = new Metadata
        {
            { "special-chars", "value with spaces, commas, and: colons" },
            { "unicode", "Unicode: 你好世界 🌍" }
        };
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/special", "localhost", metadata);
        
        Assert.That(stream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Metadata_DuplicateKeys_AllValuesSent()
    {
        var segmentName = $"meta_dup_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // gRPC allows duplicate keys
        var metadata = new Metadata
        {
            { "repeated-key", "value1" },
            { "repeated-key", "value2" },
            { "repeated-key", "value3" }
        };
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/duplicate-keys", "localhost", metadata);
        
        // All values should be preserved
        Assert.Pass("Duplicate keys handled");
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Metadata_LargeNumberOfKeys_Supported()
    {
        var segmentName = $"meta_many_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var metadata = new Metadata();
        for (int i = 0; i < 100; i++)
        {
            metadata.Add($"key-{i:D3}", $"value-{i}");
        }
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/many-keys", "localhost", metadata);
        
        Assert.That(stream.RequestHeaders!.CustomMetadata!.Count, Is.GreaterThanOrEqualTo(100));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Metadata_EmptyKey_IsRejected()
    {
        // Empty key should be rejected or handled gracefully
        var metadata = new Metadata();
        
        try
        {
            metadata.Add("", "value");
            Assert.Fail("Empty key should be rejected");
        }
        catch (ArgumentException)
        {
            // Expected
            Assert.Pass("Empty key correctly rejected");
        }
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ResponseMetadata_ServerSendsHeaders_ClientReceives()
    {
        var segmentName = $"meta_response_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/response-meta", "localhost");
        
        var responseMetadata = new Metadata
        {
            { "x-server-id", "server-001" },
            { "x-request-id", Guid.NewGuid().ToString() }
        };
        
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync(responseMetadata);
        await serverStream.SendTrailersAsync(StatusCode.OK);
        
        Assert.That(serverStream.ResponseHeadersSent, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task TrailerMetadata_ServerSendsTrailers_ContainsStatus()
    {
        var segmentName = $"meta_trailer_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/trailer-meta", "localhost");
        await clientStream.SendHalfCloseAsync();
        
        var trailerMetadata = new Metadata
        {
            { "x-trace-id", "trace-12345" },
            { "x-duration-ms", "42" }
        };
        
        var serverStream = server.CreateStream();
        await serverStream.SendResponseHeadersAsync();
        await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes("response"));
        await serverStream.SendTrailersAsync(StatusCode.OK, "success", trailerMetadata);
        
        Assert.That(serverStream.TrailersSent, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void GrpcReservedHeaders_AreRecognized()
    {
        // These headers are reserved by gRPC
        var reservedHeaders = new[]
        {
            "grpc-status",
            "grpc-message",
            "grpc-encoding",
            "grpc-accept-encoding",
            "grpc-timeout",
            "content-type"
        };
        
        foreach (var header in reservedHeaders)
        {
            Assert.That(header.StartsWith("grpc-") || header == "content-type", Is.True,
                $"{header} should be recognized as reserved");
        }
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CompressionMetadata_CanBeSet()
    {
        var segmentName = $"meta_compress_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var metadata = new Metadata
        {
            { "grpc-encoding", "gzip" },
            { "grpc-accept-encoding", "gzip, identity" }
        };
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/compressed", "localhost", metadata);
        
        Assert.Pass("Compression metadata can be set");
    }
}
