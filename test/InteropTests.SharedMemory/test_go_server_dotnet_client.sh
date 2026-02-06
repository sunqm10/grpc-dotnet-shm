#!/bin/bash
# Interop Test: Go Server + .NET Client

set -e
SEGMENT="${1:-interop_greeter}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Go Server + .NET Client (segment: $SEGMENT)"

# Build .NET client
cd "$SCRIPT_DIR/dotnet-client"
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

# Start Go server
cd "$SCRIPT_DIR/go/server"
go run server.go -segment "$SEGMENT" &
SERVER_PID=$!
sleep 2

if ! kill -0 $SERVER_PID 2>/dev/null; then
    echo "ERROR: Go server failed to start"
    exit 1
fi

# Run .NET client
cd "$SCRIPT_DIR/dotnet-client"
set +e
dotnet run -c Release --no-build -- "$SEGMENT" ".NET Client"
EXIT_CODE=$?
set -e

# Cleanup
kill $SERVER_PID 2>/dev/null || true
wait $SERVER_PID 2>/dev/null || true

exit $EXIT_CODE
