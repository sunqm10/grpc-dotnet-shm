#!/bin/bash
# Interop Test: .NET Server + Go Client

set -e
SEGMENT="${1:-interop_greeter}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo ".NET Server + Go Client (segment: $SEGMENT)"

# Build .NET server
cd "$SCRIPT_DIR/dotnet-server"
dotnet build -c Release --nologo -v q

# Generate Go protobuf
cd "$SCRIPT_DIR/go"
if [ ! -f "greetpb/greet.pb.go" ]; then
    mkdir -p greetpb
    protoc --go_out=greetpb --go-grpc_out=greetpb \
           --go_opt=paths=source_relative \
           --go-grpc_opt=paths=source_relative \
           -I .. ../greet.proto 2>/dev/null || echo "Note: protoc not available, using pre-generated files"
fi

# Start .NET server
cd "$SCRIPT_DIR/dotnet-server"
dotnet run -c Release --no-build -- "$SEGMENT" &
SERVER_PID=$!
sleep 3

if ! kill -0 $SERVER_PID 2>/dev/null; then
    echo "ERROR: .NET server failed to start"
    exit 1
fi

# Run Go client
cd "$SCRIPT_DIR/go/client"
set +e
go run client.go -segment "$SEGMENT" -name "Go Client"
EXIT_CODE=$?
set -e

# Cleanup
kill $SERVER_PID 2>/dev/null || true
wait $SERVER_PID 2>/dev/null || true

exit $EXIT_CODE
