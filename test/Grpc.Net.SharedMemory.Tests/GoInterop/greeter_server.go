//go:build linux || windows
// +build linux windows

/*
 * Greeter server for .NET interop testing.
 * Creates a shared memory segment and serves gRPC requests.
 *
 * Usage:
 *   go run greeter_server.go -segment <name>
 *
 * Test with .NET client:
 *   cd examples/Greeter.SharedMemory/Client
 *   dotnet run -- <segment_name>
 */

package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
)

var (
	segment = flag.String("segment", "greeter_interop", "Shared memory segment name")
)

// Simple mock server that reads frames and writes responses
// In a real implementation, this would use grpc-go-shmem's transport
func main() {
	flag.Parse()

	fmt.Printf("Go Greeter Server - Shared Memory Interop Test\n")
	fmt.Printf("===============================================\n\n")
	fmt.Printf("Segment name: %s\n", *segment)
	fmt.Printf("\nTo test with .NET client:\n")
	fmt.Printf("  1. Edit examples/Greeter.SharedMemory/Client/Program.cs\n")
	fmt.Printf("     Change SegmentName to \"%s\"\n", *segment)
	fmt.Printf("  2. Run: dotnet run\n\n")

	// For a complete implementation, you would:
	// 1. Import github.com/markrussinovich/grpc-go-shmem/shm
	// 2. Create a listener: lis, _ := shm.Listen(*segment)
	// 3. Create gRPC server and register services
	// 4. s.Serve(lis)

	fmt.Printf("NOTE: This is a stub. For full interop testing:\n")
	fmt.Printf("  1. Clone https://github.com/markrussinovich/grpc-go-shmem\n")
	fmt.Printf("  2. Use the examples/shm/greeter server\n\n")

	// Wait for interrupt
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	fmt.Println("\nShutting down...")
}

// HelloRequest mirrors the protobuf message
type HelloRequest struct {
	Name string
}

// HelloReply mirrors the protobuf message
type HelloReply struct {
	Message string
}

// SayHello implements the Greeter service
func SayHello(ctx context.Context, req *HelloRequest) (*HelloReply, error) {
	return &HelloReply{
		Message: fmt.Sprintf("Hello from Go server: %s", req.Name),
	}, nil
}
