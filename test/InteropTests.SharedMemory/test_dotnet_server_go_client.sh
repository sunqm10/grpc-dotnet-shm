#!/bin/bash
# Interop Test: .NET Server + Go Client
# Uses the grpc-go-shmem helloworld example client

set -e
SEGMENT="${1:-interop_helloworld}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GRPC_GO_SHMEM="/workspaces/grpc-go-shmem"

echo "========================================"
echo ".NET Server + Go Client Interop Test"
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

# Build .NET server
echo "Building .NET server..."
cd "$SCRIPT_DIR/dotnet-server"
dotnet build -c Release --nologo -v q

# Build Go client from grpc-go-shmem
echo "Building Go client..."
cd "$GRPC_GO_SHMEM/examples/shm/helloworld/greeter_client"
go build -o /tmp/greeter_client_interop .

# Start .NET server
echo "Starting .NET server on shm://$SEGMENT..."
cd "$SCRIPT_DIR/dotnet-server"
dotnet run -c Release --no-build -- "$SEGMENT" &
SERVER_PID=$!
sleep 3

if ! kill -0 $SERVER_PID 2>/dev/null; then
    echo "ERROR: .NET server failed to start"
    exit 1
fi

# Run Go client
echo ""
echo "Running Go client..."
set +e
/tmp/greeter_client_interop -addr "shm://$SEGMENT" -name "GoClient"
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
    echo "TEST PASSED: .NET Server + Go Client"
    echo "========================================"
else
    echo ""
    echo "========================================"
    echo "TEST FAILED: .NET Server + Go Client"
    echo "========================================"
fi

exit $EXIT_CODE
