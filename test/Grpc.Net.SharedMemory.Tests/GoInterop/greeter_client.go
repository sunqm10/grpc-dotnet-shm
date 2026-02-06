//go:build linux || windows
// +build linux windows

/*
 * Greeter client for .NET interop testing.
 * Connects to a shared memory segment and sends gRPC requests.
 *
 * Usage:
 *   go run greeter_client.go -segment <name>
 *
 * Test with .NET server:
 *   cd examples/Greeter.SharedMemory/Server
 *   dotnet run
 *   # Then run this client with the same segment name
 */

package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"time"
)

var (
	segment = flag.String("segment", "greeter_shm_example", "Shared memory segment name")
	name    = flag.String("name", "Go Client", "Name to send in request")
)

func main() {
	flag.Parse()

	fmt.Printf("Go Greeter Client - Shared Memory Interop Test\n")
	fmt.Printf("===============================================\n\n")
	fmt.Printf("Segment name: %s\n", *segment)
	fmt.Printf("Name: %s\n\n", *name)

	fmt.Printf("To test with .NET server:\n")
	fmt.Printf("  1. Start .NET server:\n")
	fmt.Printf("     cd examples/Greeter.SharedMemory/Server && dotnet run\n")
	fmt.Printf("  2. Run this client with matching segment name\n\n")

	// For a complete implementation, you would:
	// 1. Import github.com/markrussinovich/grpc-go-shmem/shm
	// 2. Create a connection: conn, _ := shm.Dial(*segment)
	// 3. Create client: client := pb.NewGreeterClient(conn)
	// 4. Call: reply, _ := client.SayHello(ctx, &pb.HelloRequest{Name: *name})

	fmt.Printf("NOTE: This is a stub. For full interop testing:\n")
	fmt.Printf("  1. Clone https://github.com/markrussinovich/grpc-go-shmem\n")
	fmt.Printf("  2. Use the examples/shm/greeter client\n")
	fmt.Printf("  3. Or build a client using the shm package\n\n")

	// Example of what the full implementation would look like:
	fmt.Printf("Example code:\n")
	fmt.Printf("  conn, err := shm.Dial(\"%s\")\n", *segment)
	fmt.Printf("  client := pb.NewGreeterClient(conn)\n")
	fmt.Printf("  reply, err := client.SayHello(ctx, &pb.HelloRequest{Name: \"%s\"})\n", *name)
	fmt.Printf("  fmt.Println(reply.Message)\n")
}

// HelloRequest mirrors the protobuf message
type HelloRequest struct {
	Name string
}

// HelloReply mirrors the protobuf message
type HelloReply struct {
	Message string
}

// mockSayHello simulates a call (for documentation purposes)
func mockSayHello(ctx context.Context, req *HelloRequest) (*HelloReply, error) {
	return &HelloReply{
		Message: fmt.Sprintf("Hello %s from .NET!", req.Name),
	}, nil
}
