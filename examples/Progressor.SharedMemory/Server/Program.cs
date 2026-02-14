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

using Google.Protobuf.WellKnownTypes;
using Grpc.Net.SharedMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Progress;
using Server;

// Create the service (same implementation as the TCP Progressor example)
var service = new ProgressorService(NullLoggerFactory.Instance);

// Create SHM gRPC server — direct SHM transport per A73
await using var server = new ShmGrpcServer("progressor_shm_example");

server.MapServerStreaming<Empty, HistoryResponse>(
    "/progress.Progressor/RunHistory", service.RunHistory);

await server.RunAsync();
