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

using Grpc.Core;
using Upload;

namespace Server;

/// <summary>
/// Uploader service that receives streamed file content over shared memory.
/// </summary>
public class UploaderService : Uploader.UploaderBase
{
    private readonly ILogger _logger;
    private readonly IConfiguration _config;

    public UploaderService(ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = loggerFactory.CreateLogger<UploaderService>();
        _config = config;
    }

    public override async Task<UploadFileResponse> UploadFile(
        IAsyncStreamReader<UploadFileRequest> requestStream,
        ServerCallContext context)
    {
        var uploadId = Path.GetRandomFileName();
        var uploadPath = Path.Combine(_config["StoredFilesPath"]!, uploadId);
        Directory.CreateDirectory(uploadPath);

        _logger.LogInformation("Starting upload: {UploadId}", uploadId);

        await using var writeStream = File.Create(Path.Combine(uploadPath, "data.bin"));

        await foreach (var message in requestStream.ReadAllAsync())
        {
            if (message.Metadata != null)
            {
                _logger.LogInformation("Received metadata: {FileName}", message.Metadata.FileName);
                await File.WriteAllTextAsync(
                    Path.Combine(uploadPath, "metadata.json"),
                    message.Metadata.ToString());
            }
            if (message.Data != null)
            {
                _logger.LogInformation("Received data chunk of {Length} bytes", message.Data.Length);
                await writeStream.WriteAsync(message.Data.Memory);
            }
        }

        _logger.LogInformation("Upload complete: {UploadId}", uploadId);
        return new UploadFileResponse { Id = uploadId };
    }
}
