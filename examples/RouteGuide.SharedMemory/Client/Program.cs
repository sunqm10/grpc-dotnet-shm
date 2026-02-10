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
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using RouteGuide;

Console.WriteLine("RouteGuide Shared Memory Client");
Console.WriteLine("===============================");
Console.WriteLine();

const string SegmentName = "routeguide_shm_example";

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    // Create channel using shared memory handler
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmHttpHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new RouteGuide.RouteGuide.RouteGuideClient(channel);

    // ============================================================
    // 1. Unary RPC: GetFeature
    // ============================================================
    Console.WriteLine("=== GetFeature (Unary RPC) ===");

    // Looking for a valid feature
    await PrintFeature(client, new Point { Latitude = 409146138, Longitude = -746188906 });

    // Feature missing
    await PrintFeature(client, new Point { Latitude = 0, Longitude = 0 });

    Console.WriteLine();

    // ============================================================
    // 2. Server Streaming RPC: ListFeatures
    // ============================================================
    Console.WriteLine("=== ListFeatures (Server Streaming RPC) ===");
    Console.WriteLine("Looking for features between 40, -75 and 42, -73");

    await PrintFeatures(client, new Rectangle
    {
        Lo = new Point { Latitude = 400000000, Longitude = -750000000 },
        Hi = new Point { Latitude = 420000000, Longitude = -730000000 }
    });

    Console.WriteLine();

    // ============================================================
    // 3. Client Streaming RPC: RecordRoute
    // ============================================================
    Console.WriteLine("=== RecordRoute (Client Streaming RPC) ===");

    await RunRecordRoute(client);

    Console.WriteLine();

    // ============================================================
    // 4. Bidirectional Streaming RPC: RouteChat
    // ============================================================
    Console.WriteLine("=== RouteChat (Bidirectional Streaming RPC) ===");

    await RunRouteChat(client);

    Console.WriteLine();
    Console.WriteLine("All examples completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/RouteGuide.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

// ============================================================
// Helper Functions
// ============================================================

static async Task PrintFeature(RouteGuide.RouteGuide.RouteGuideClient client, Point point)
{
    var feature = await client.GetFeatureAsync(point);
    if (string.IsNullOrEmpty(feature.Name))
    {
        Console.WriteLine($"Feature: name: \"\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
    }
    else
    {
        Console.WriteLine($"Feature: name: \"{feature.Name}\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
    }
}

static async Task PrintFeatures(RouteGuide.RouteGuide.RouteGuideClient client, Rectangle rect)
{
    using var call = client.ListFeatures(rect);

    var count = 0;
    await foreach (var feature in call.ResponseStream.ReadAllAsync())
    {
        Console.WriteLine($"  Feature: \"{feature.Name}\" at ({feature.Location.Latitude}, {feature.Location.Longitude})");
        count++;
    }
    Console.WriteLine($"Listed {count} features");
}

static async Task RunRecordRoute(RouteGuide.RouteGuide.RouteGuideClient client)
{
    using var call = client.RecordRoute();

    // Generate random points
    var random = new Random(42);
    var pointCount = random.Next(5, 15);
    Console.WriteLine($"Traversing {pointCount} points.");

    for (var i = 0; i < pointCount; i++)
    {
        var point = new Point
        {
            Latitude = random.Next(400000000, 420000000),
            Longitude = random.Next(-750000000, -730000000)
        };
        await call.RequestStream.WriteAsync(point);
    }

    await call.RequestStream.CompleteAsync();

    var summary = await call;
    Console.WriteLine($"Route summary: point_count:{summary.PointCount} feature_count:{summary.FeatureCount} distance:{summary.Distance} elapsed_time:{summary.ElapsedTime}");
}

static async Task RunRouteChat(RouteGuide.RouteGuide.RouteGuideClient client)
{
    using var call = client.RouteChat();

    var notes = new[]
    {
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "First message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Second message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Third message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "Fourth message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Fifth message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Sixth message" },
    };

    // Start a task to receive notes
    var receiveTask = Task.Run(async () =>
    {
        await foreach (var note in call.ResponseStream.ReadAllAsync())
        {
            Console.WriteLine($"Got message \"{note.Message}\" at point({note.Location.Latitude}, {note.Location.Longitude})");
        }
    });

    // Send notes
    foreach (var note in notes)
    {
        await call.RequestStream.WriteAsync(note);
        await Task.Delay(100);
    }

    await call.RequestStream.CompleteAsync();

    // Wait for receive to complete
    await receiveTask;
}
