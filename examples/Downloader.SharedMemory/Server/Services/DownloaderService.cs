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

using Download;
using Google.Protobuf;
using Grpc.Core;

namespace Server.Services;

/// <summary>
/// Downloader service that streams file content over shared memory.
/// </summary>
public class DownloaderService : Downloader.DownloaderBase
{
    private const int ChunkSize = 32 * 1024; // 32KB chunks

    public override async Task DownloadFile(DownloadFileRequest request, IServerStreamWriter<DownloadFileResponse> responseStream, ServerCallContext context)
    {
        var filename = "sample.txt";

        // Send metadata first
        await responseStream.WriteAsync(new DownloadFileResponse
        {
            Metadata = new FileMetadata { FileName = filename }
        });
        Console.WriteLine($"Sent metadata for file: {filename}");

        // Stream file content in chunks
        var buffer = new byte[ChunkSize];
        await using var fileStream = File.OpenRead(filename);
        long totalBytesSent = 0;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            var numBytesRead = await fileStream.ReadAsync(buffer, context.CancellationToken);
            if (numBytesRead == 0)
            {
                break;
            }

            Console.WriteLine($"Sending data chunk of {numBytesRead} bytes");

            await responseStream.WriteAsync(new DownloadFileResponse
            {
                Data = UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, numBytesRead))
            });
            totalBytesSent += numBytesRead;
        }

        Console.WriteLine($"File download complete. Sent {totalBytesSent} bytes total.");
    }
}
