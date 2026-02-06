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
using Greet;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Google.Protobuf;

namespace Server.Services;

/// <summary>
/// Simple Greeter service that processes requests over shared memory.
/// This demonstrates the shared memory transport pattern without full ASP.NET Core integration.
/// </summary>
public class GreeterService
{
    /// <summary>
    /// Handles an incoming SayHello request.
    /// </summary>
    /// <param name="stream">The gRPC stream from the client.</param>
    /// <param name="requestData">The serialized request message.</param>
    /// <returns>The serialized response message.</returns>
    public Task<byte[]> SayHelloAsync(ShmGrpcStream stream, ReadOnlyMemory<byte> requestData)
    {
        // Deserialize the request
        var request = HelloRequest.Parser.ParseFrom(requestData.Span);

        Console.WriteLine($"Received greeting request from: {request.Name}");

        // Process the request (simple greeting logic)
        var response = new HelloReply
        {
            Message = $"Hello {request.Name}!"
        };

        // Serialize and return the response
        return Task.FromResult(response.ToByteArray());
    }

    /// <summary>
    /// Routes an incoming gRPC method call to the appropriate handler.
    /// </summary>
    /// <param name="stream">The gRPC stream.</param>
    /// <param name="method">The method name (e.g., "/greet.Greeter/SayHello").</param>
    /// <param name="requestData">The serialized request data.</param>
    /// <returns>The serialized response data.</returns>
    public Task<byte[]> HandleMethodAsync(ShmGrpcStream stream, string method, ReadOnlyMemory<byte> requestData)
    {
        return method switch
        {
            "/greet.Greeter/SayHello" => SayHelloAsync(stream, requestData),
            _ => throw new RpcException(new Status(StatusCode.Unimplemented, $"Method {method} is not implemented"))
        };
    }
}
