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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Thrown when a server refuses a stream before sending response headers.
/// Analogous to HTTP/2 RST_STREAM with REFUSED_STREAM error code.
/// <para>
/// This typically occurs when the server's maximum concurrent stream limit
/// is reached and it rejects the stream by sending TRAILERS (with
/// <see cref="Grpc.Core.StatusCode.Unavailable"/>) directly, without
/// preceding HEADERS.
/// </para>
/// </summary>
/// <remarks>
/// With atomic stream counting in <see cref="ShmConnection.CreateStream"/>,
/// the client enforces maxStreams before sending any frames, so this exception
/// should not occur in normal operation. It serves as a safety net for edge
/// cases (e.g., server-side configuration mismatch).
/// <see cref="ShmControlHandler"/> does NOT retry on this exception because
/// the request body may have already been partially consumed.
/// </remarks>
public sealed class ShmStreamRefusedException : InvalidOperationException
{
    /// <summary>
    /// Creates a new <see cref="ShmStreamRefusedException"/>.
    /// </summary>
    /// <param name="message">The refusal reason from the server.</param>
    public ShmStreamRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="ShmStreamRefusedException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The refusal reason.</param>
    /// <param name="innerException">The inner exception.</param>
    public ShmStreamRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when <see cref="ShmConnection.CreateStream"/> fails because the
/// atomic stream counter has reached <see cref="ShmConnection.MaxConcurrentStreams"/>.
/// <para>
/// This is a transient, recoverable condition: another thread consumed the last
/// slot between <see cref="ShmConnectionPool.GetConnectionAsync"/> returning and
/// <see cref="ShmConnection.CreateStream"/> executing. The caller should retry
/// via the pool, which will either find/create a connection with capacity.
/// </para>
/// </summary>
/// <remarks>
/// Unlike <see cref="InvalidOperationException"/> (which also covers disposed,
/// GoAway, and other non-recoverable states), this exception is safe to catch
/// and retry without risk of masking unrelated errors.
/// </remarks>
public sealed class ShmStreamCapacityExceededException : InvalidOperationException
{
    /// <summary>
    /// Creates a new <see cref="ShmStreamCapacityExceededException"/>.
    /// </summary>
    public ShmStreamCapacityExceededException(string message)
        : base(message)
    {
    }
}
