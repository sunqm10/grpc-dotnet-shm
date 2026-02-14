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
using Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Server;

// Create dependencies (same as TCP Mailer example)
var repo = new MailQueueRepository();
var service = new MailerService(NullLoggerFactory.Instance, repo);

// Create SHM gRPC server — direct SHM transport per A73
await using var server = new ShmGrpcServer("mailer_shm_example");

server.MapDuplexStreaming<ForwardMailMessage, MailboxMessage>(
    "/mail.Mailer/Mailbox", service.Mailbox);

await server.RunAsync();
