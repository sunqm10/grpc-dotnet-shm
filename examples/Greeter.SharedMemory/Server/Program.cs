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

using Greet;
using Grpc.Net.SharedMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Server;

// Create the service (same implementation as the TCP Greeter example)
var service = new GreeterService(NullLoggerFactory.Instance);

// Create SHM gRPC server — the only difference from TCP is using ShmGrpcServer
// instead of WebApplication with Kestrel. Per A73, SHM transport exposes gRPC
// semantics directly (headers, messages, trailers) without HTTP/2 framing.
await using var server = new ShmGrpcServer("greeter_shm_example");

server.MapUnary<HelloRequest, HelloReply>(
    "/greet.Greeter/SayHello", service.SayHello);

await server.RunAsync();
