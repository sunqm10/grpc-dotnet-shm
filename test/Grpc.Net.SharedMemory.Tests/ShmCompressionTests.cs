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

using Grpc.Net.SharedMemory.Compression;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmCompressionTests
{
    [Test]
    public void GzipCompressor_CompressDecompress_RoundTrips()
    {
        // Arrange
        var compressor = new GzipCompressor();
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Act
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(decompressed, Is.EqualTo(original));
    }

    [Test]
    public void GzipCompressor_HasCorrectName()
    {
        // Arrange
        var compressor = new GzipCompressor();

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("gzip"));
    }

    [Test]
    public void DeflateCompressor_CompressDecompress_RoundTrips()
    {
        // Arrange
        var compressor = new DeflateCompressor();
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Act
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(decompressed, Is.EqualTo(original));
    }

    [Test]
    public void DeflateCompressor_HasCorrectName()
    {
        // Arrange
        var compressor = new DeflateCompressor();

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("deflate"));
    }

    [Test]
    public void IdentityCompressor_PassesThrough()
    {
        // Arrange
        var compressor = new IdentityCompressor();
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Act
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(compressed, Is.EqualTo(original));
        Assert.That(decompressed, Is.EqualTo(original));
    }

    [Test]
    public void IdentityCompressor_HasCorrectName()
    {
        // Arrange
        var compressor = new IdentityCompressor();

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("identity"));
    }

    [Test]
    public void Registry_GetCompressor_ReturnsGzip()
    {
        // Act
        var compressor = ShmCompressorRegistry.Get("gzip");

        // Assert
        Assert.That(compressor, Is.Not.Null);
        Assert.That(compressor!.Name, Is.EqualTo("gzip"));
    }

    [Test]
    public void Registry_GetCompressor_ReturnsDeflate()
    {
        // Act
        var compressor = ShmCompressorRegistry.Get("deflate");

        // Assert
        Assert.That(compressor, Is.Not.Null);
        Assert.That(compressor!.Name, Is.EqualTo("deflate"));
    }

    [Test]
    public void Registry_GetCompressor_ReturnsIdentity()
    {
        // Act
        var compressor = ShmCompressorRegistry.Get("identity");

        // Assert
        Assert.That(compressor, Is.Not.Null);
        Assert.That(compressor!.Name, Is.EqualTo("identity"));
    }

    [Test]
    public void Registry_GetCompressor_ReturnsNullForUnknown()
    {
        // Act
        var compressor = ShmCompressorRegistry.Get("unknown");

        // Assert
        Assert.That(compressor, Is.Null);
    }

    [Test]
    public void Registry_RegisterCustomCompressor()
    {
        // Arrange
        var customCompressor = new TestCompressor("test-comp");

        // Act
        ShmCompressorRegistry.Register(customCompressor);
        var retrieved = ShmCompressorRegistry.Get("test-comp");

        // Assert
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Name, Is.EqualTo("test-comp"));
    }

    [Test]
    public void CompressionOptions_Default_HasSensibleDefaults()
    {
        // Act
        var options = ShmCompressionOptions.Default;

        // Assert
        Assert.That(options.SendCompress, Is.EqualTo("identity"));
        Assert.That(options.AcceptedCompressors, Is.Not.Empty);
    }

    [Test]
    public void CompressionOptions_Gzip_UsesGzip()
    {
        // Act
        var options = ShmCompressionOptions.Gzip;

        // Assert
        Assert.That(options.SendCompress, Is.EqualTo("gzip"));
    }

    [Test]
    public void CompressionOptions_ShouldCompress_ReturnsFalse_ForSmallData()
    {
        // Arrange
        var options = new ShmCompressionOptions 
        { 
            SendCompress = "gzip",
            MinSizeForCompression = 100 
        };
        var smallData = new byte[50];

        // Act
        var shouldCompress = options.ShouldCompress(smallData);

        // Assert
        Assert.That(shouldCompress, Is.False);
    }

    [Test]
    public void CompressionOptions_ShouldCompress_ReturnsTrue_ForLargeData()
    {
        // Arrange
        var options = new ShmCompressionOptions 
        { 
            SendCompress = "gzip",
            MinSizeForCompression = 100 
        };
        var largeData = new byte[200];

        // Act
        var shouldCompress = options.ShouldCompress(largeData);

        // Assert
        Assert.That(shouldCompress, Is.True);
    }

    [Test]
    public void CompressionOptions_GetAcceptEncoding_ReturnsCommaSeparated()
    {
        // Arrange
        var options = new ShmCompressionOptions
        {
            AcceptedCompressors = new[] { "gzip", "deflate", "identity" }
        };

        // Act
        var acceptEncoding = options.GetAcceptEncoding();

        // Assert
        Assert.That(acceptEncoding, Does.Contain("gzip"));
        Assert.That(acceptEncoding, Does.Contain("deflate"));
        Assert.That(acceptEncoding, Does.Contain("identity"));
    }

    private class TestCompressor : IShmCompressor
    {
        public TestCompressor(string name) => Name = name;
        public string Name { get; }
        public byte[] Compress(byte[] data) => data;
        public byte[] Decompress(byte[] data) => data;
    }
}
