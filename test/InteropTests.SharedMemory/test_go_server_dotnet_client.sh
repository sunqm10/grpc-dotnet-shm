#!/bin/bash
# Interop Test: Go Server + .NET Client
# Uses the grpc-go-shmem helloworld example server

set -e
SEGMENT="${1:-interop_helloworld}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GRPC_GO_SHMEM="/workspaces/grpc-go-shmem"

echo "========================================"
echo "Go Server + .NET Client Interop Test"
echo "========================================"
echo "Segment: $SEGMENT"
echo ""

# Check if grpc-go-shmem is available
if [ ! -d "$GRPC_GO_SHMEM" ]; then
    echo "ERROR: grpc-go-shmem not found at $GRPC_GO_SHMEM"
    echo "Please clone it first:"
    echo "  git clone https://github.com/markrussinovich/grpc-go-shmem.git $GRPC_GO_SHMEM"
    exit 1
fi

# Build .NET client
echo "Building .NET client..."
cd "$SCRIPT_DIR/dotnet-client"
dotnet build -c Release --nologo -v q

# Build Go server from grpc-go-shmem
echo "Building Go server..."
cd "$GRPC_GO_SHMEM/examples/shm/helloworld/greeter_server"
go build -o /tmp/greeter_server_interop .

# Start Go server
echo "Starting Go server on shm://$SEGMENT..."
/tmp/greeter_server_interop -addr "shm://$SEGMENT" &
SERVER_PID=$!
sleep 2

if ! kill -0 $SERVER_PID 2>/dev/null; then
    echo "ERROR: Go server failed to start"
    exit 1
fi

# Run .NET client
echo ""
echo "Running .NET client..."
cd "$SCRIPT_DIR/dotnet-client"
set +e
dotnet run -c Release --no-build -- "$SEGMENT" ".NET Client"
EXIT_CODE=$?
set -e

# Cleanup
echo ""
echo "Cleaning up..."
kill $SERVER_PID 2>/dev/null || true
wait $SERVER_PID 2>/dev/null || true

# Clean up shared memory segment
rm -f "/dev/shm/$SEGMENT"* 2>/dev/null || true

if [ $EXIT_CODE -eq 0 ]; then
    echo ""
    echo "========================================"
    echo "TEST PASSED: Go Server + .NET Client"
    echo "========================================"
else
    echo ""
    echo "========================================"
    echo "TEST FAILED: Go Server + .NET Client"
    echo "========================================"
fi

exit $EXIT_CODE
