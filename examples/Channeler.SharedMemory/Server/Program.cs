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

using DataChannel;
using Grpc.Net.SharedMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Server;

// Create the service (same implementation as the TCP Channeler example)
var service = new DataChannelerService(NullLoggerFactory.Instance);

// Create SHM gRPC server — direct SHM transport per A73
await using var server = new ShmGrpcServer("channeler_shm_example");

server.MapClientStreaming<DataRequest, DataResult>(
    "/data_channel.DataChanneler/UploadData", service.UploadData);
server.MapServerStreaming<DataRequest, DataResult>(
    "/data_channel.DataChanneler/DownloadResults", service.DownloadResults);

await server.RunAsync();
