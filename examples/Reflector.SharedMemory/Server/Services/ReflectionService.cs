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

using Google.Protobuf;
using Grpc.Net.SharedMemory;
using Grpc.Reflection.V1Alpha;

namespace Server.Services;

/// <summary>
/// Server reflection service that lists available services.
/// This is a simplified implementation for demonstration purposes.
/// </summary>
public class ReflectionService
{
    private readonly List<string> _services = new()
    {
        "greet.Greeter",
        "grpc.reflection.v1alpha.ServerReflection"
    };

    /// <summary>
    /// Handles reflection requests over bidirectional streaming.
    /// </summary>
    public async Task HandleReflectionAsync(ShmGrpcStream stream, CancellationToken cancellationToken)
    {
        // Read the request using ReceiveFrameAsync
        var frame = await stream.ReceiveFrameAsync(cancellationToken);
        if (frame == null)
        {
            return;
        }

        var (frameType, payload) = frame.Value;
        if (frameType != FrameType.Message || payload.Length == 0)
        {
            return;
        }

        var request = ServerReflectionRequest.Parser.ParseFrom(payload);
        Console.WriteLine($"Reflection request: {request.MessageRequestCase}");

        var response = new ServerReflectionResponse
        {
            OriginalRequest = request
        };

        switch (request.MessageRequestCase)
        {
            case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                response.ListServicesResponse = new ListServiceResponse();
                foreach (var service in _services)
                {
                    response.ListServicesResponse.Service.Add(new ServiceResponse { Name = service });
                }
                break;

            case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                // For a full implementation, this would return the proto file descriptor
                response.ErrorResponse = new ErrorResponse
                {
                    ErrorCode = (int)Grpc.Core.StatusCode.Unimplemented,
                    ErrorMessage = "FileContainingSymbol not implemented in this demo"
                };
                break;

            default:
                response.ErrorResponse = new ErrorResponse
                {
                    ErrorCode = (int)Grpc.Core.StatusCode.Unimplemented,
                    ErrorMessage = $"Request type {request.MessageRequestCase} not implemented"
                };
                break;
        }

        await stream.SendMessageAsync(response.ToByteArray());
    }
}
