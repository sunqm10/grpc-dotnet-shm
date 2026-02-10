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

using Grpc.AspNetCore.Server.SharedMemory;
using Server;

const string SegmentName = "uploader_shm_example";

Console.WriteLine("File Uploader - Shared Memory Server");
Console.WriteLine("=====================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create uploads directory
var uploadsPath = Path.Combine(Environment.CurrentDirectory, "uploads");
Directory.CreateDirectory(uploadsPath);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Configuration["StoredFilesPath"] = uploadsPath;
builder.WebHost.UseSharedMemory(SegmentName);

var app = builder.Build();
app.MapGrpcService<UploaderService>();

Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
Console.WriteLine($"Uploads will be saved to: {uploadsPath}");
Console.WriteLine("Press Ctrl+C to stop the server.");

await app.RunAsync();
