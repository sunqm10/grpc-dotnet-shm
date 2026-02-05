//go:build linux || windows
// +build linux windows

/*
 * Greeter client for .NET interop testing.
 * Connects to a shared memory segment and sends gRPC requests.
 *
 * Usage:
 *   go run greeter_client.go -segment <name> -name <greeting>
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
	"log"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	pb "google.golang.org/grpc/examples/helloworld/helloworld"
)

const (
	defaultName = "World"
)

var (
	segment = flag.String("segment", "interop_test_shm", "Shared memory segment name")
	name    = flag.String("name", defaultName, "Name to greet")
)

func main() {
	flag.Parse()
	
	addr := "shm://" + *segment
	
	log.Printf("Connecting to %s...", addr)
	
	// Set up a connection to the server using shared memory transport
	conn, err := grpc.NewClient(addr,
		grpc.WithShmTransport(),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
	)
	if err != nil {
		log.Fatalf("did not connect: %v", err)
	}
	defer conn.Close()
	
	c := pb.NewGreeterClient(conn)

	// Contact the server and print out its response.
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	
	r, err := c.SayHello(ctx, &pb.HelloRequest{Name: *name})
	if err != nil {
		log.Fatalf("could not greet: %v", err)
	}
	log.Printf("Greeting: %s", r.GetMessage())
	log.Printf("SUCCESS: Go client received response from server")
}
