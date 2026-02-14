#!/bin/bash
#
# E2E Interop Test: Go (grpc-go-shmem) <-> .NET (Grpc.Net.SharedMemory)
#
# This script tests bidirectional interop between Go and .NET shared memory gRPC.
#
# Prerequisites:
#   1. Go 1.21+ installed
#   2. grpc-go-shmem cloned to /tmp/grpc-go-shmem
#   3. .NET SDK 9.0+ installed
#
# Usage:
#   ./run_interop_test.sh [go-server|dotnet-server|both]
#

# Don't exit on errors - we want to run all tests
set +e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GO_SHMEM_DIR="/tmp/grpc-go-shmem"
TIMEOUT=30  # Increased for slower machines

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

cleanup_segments() {
    # Remove segment files
    rm -f /dev/shm/grpc_shm_interop_test_* 2>/dev/null || true
    rm -f /tmp/grpc_shm_interop_test_* 2>/dev/null || true
}

check_prereqs() {
    log_info "Checking prerequisites..."
    
    if ! command -v go &> /dev/null; then
        log_error "Go is not installed. Please install Go 1.21+"
        exit 1
    fi
    
    if ! command -v dotnet &> /dev/null; then
        log_error ".NET SDK is not installed. Please install .NET 9.0+"
        exit 1
    fi
    
    if [ ! -d "$GO_SHMEM_DIR" ]; then
        log_warn "grpc-go-shmem not found at $GO_SHMEM_DIR"
        log_info "Cloning grpc-go-shmem..."
        git clone https://github.com/markrussinovich/grpc-go-shmem.git "$GO_SHMEM_DIR"
    fi
    
    log_info "Prerequisites OK"
}

build_dotnet() {
    log_info "Building .NET interop tests..."
    cd "$SCRIPT_DIR"
    dotnet build InteropClient/InteropClient.csproj -c Release --nologo -v q
    dotnet build InteropServer/InteropServer.csproj -c Release --nologo -v q
}

# Pre-build Go examples to avoid compile time during test
build_go() {
    log_info "Building Go examples..."
    cd "$GO_SHMEM_DIR/examples/shm/helloworld/greeter_server"
    go build -o /tmp/go_greeter_server . 2>/dev/null || true
    cd "$GO_SHMEM_DIR/examples/shm/helloworld/greeter_client"
    go build -o /tmp/go_greeter_client . 2>/dev/null || true
}

wait_for_segment() {
    local segment_name="$1"
    local max_wait=10
    local waited=0
    
    while [ $waited -lt $max_wait ]; do
        if [ -f "/dev/shm/grpc_shm_${segment_name}_ctl" ] || [ -f "/tmp/grpc_shm_${segment_name}_ctl" ]; then
            return 0
        fi
        sleep 1
        waited=$((waited + 1))
    done
    return 1
}

test_go_server_dotnet_client() {
    local SEGMENT_NAME="interop_test_go_$$"
    log_info "=== Test 1: Go Server + .NET Client ==="
    
    cleanup_segments
    
    # Start Go server in background (use pre-built binary if available)
    log_info "Starting Go server (segment: $SEGMENT_NAME)..."
    cd "$GO_SHMEM_DIR/examples/shm/helloworld/greeter_server"
    if [ -x /tmp/go_greeter_server ]; then
        /tmp/go_greeter_server -addr "shm://$SEGMENT_NAME" &
    else
        go run . -addr "shm://$SEGMENT_NAME" &
    fi
    local GO_PID=$!
    
    # Wait for segment to appear
    log_info "Waiting for Go server to be ready..."
    if ! wait_for_segment "$SEGMENT_NAME"; then
        log_error "Go server failed to create segment"
        kill $GO_PID 2>/dev/null || true
        return 1
    fi
    
    if ! kill -0 $GO_PID 2>/dev/null; then
        log_error "Go server process died"
        return 1
    fi
    
    # Run .NET client
    log_info "Running .NET client..."
    cd "$SCRIPT_DIR/InteropClient"
    local result=0
    if timeout $TIMEOUT dotnet run -c Release --no-build -- "$SEGMENT_NAME" "DotNetClient"; then
        log_info "Test 1 PASSED: .NET client successfully called Go server"
    else
        log_error "Test 1 FAILED: .NET client could not call Go server"
        result=1
    fi
    
    kill $GO_PID 2>/dev/null || true
    wait $GO_PID 2>/dev/null || true
    cleanup_segments
    return $result
}

test_dotnet_server_go_client() {
    local SEGMENT_NAME="interop_test_dn_$$"
    log_info "=== Test 2: .NET Server + Go Client ==="
    
    cleanup_segments
    
    # Start .NET server in background
    log_info "Starting .NET server (segment: $SEGMENT_NAME)..."
    cd "$SCRIPT_DIR/InteropServer"
    dotnet run -c Release --no-build -- "$SEGMENT_NAME" &
    local DOTNET_PID=$!
    
    # Wait for segment to appear
    log_info "Waiting for .NET server to be ready..."
    if ! wait_for_segment "$SEGMENT_NAME"; then
        log_error ".NET server failed to create segment"
        kill $DOTNET_PID 2>/dev/null || true
        return 1
    fi
    
    if ! kill -0 $DOTNET_PID 2>/dev/null; then
        log_error ".NET server process died"
        return 1
    fi
    
    # Run Go client (use pre-built binary if available)
    log_info "Running Go client..."
    cd "$GO_SHMEM_DIR/examples/shm/helloworld/greeter_client"
    local result=0
    if [ -x /tmp/go_greeter_client ]; then
        if timeout $TIMEOUT /tmp/go_greeter_client -addr "shm://$SEGMENT_NAME" -name "GoClient"; then
            log_info "Test 2 PASSED: Go client successfully called .NET server"
        else
            log_error "Test 2 FAILED: Go client could not call .NET server"
            result=1
        fi
    else
        if timeout $TIMEOUT go run . -addr "shm://$SEGMENT_NAME" -name "GoClient"; then
            log_info "Test 2 PASSED: Go client successfully called .NET server"
        else
            log_error "Test 2 FAILED: Go client could not call .NET server"
            result=1
        fi
    fi
    
    kill $DOTNET_PID 2>/dev/null || true
    wait $DOTNET_PID 2>/dev/null || true
    cleanup_segments
    return $result
}

main() {
    local mode="${1:-both}"
    
    echo "========================================"
    echo "Go <-> .NET Shared Memory Interop Test"
    echo "========================================"
    echo
    
    check_prereqs
    build_go
    build_dotnet
    
    local passed=0
    local failed=0
    
    case "$mode" in
        go-server)
            if test_go_server_dotnet_client; then ((passed++)); else ((failed++)); fi
            ;;
        dotnet-server)
            if test_dotnet_server_go_client; then ((passed++)); else ((failed++)); fi
            ;;
        both|*)
            if test_go_server_dotnet_client; then ((passed++)); else ((failed++)); fi
            sleep 1
            if test_dotnet_server_go_client; then ((passed++)); else ((failed++)); fi
            ;;
    esac
    
    echo
    echo "========================================"
    echo "Results: $passed passed, $failed failed"
    echo "========================================"
    
    if [ $failed -eq 0 ]; then
        log_info "All tests passed!"
        exit 0
    else
        log_error "Some tests failed"
        exit 1
    fi
}

main "$@"
