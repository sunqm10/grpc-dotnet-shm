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
	"log"
	"strings"

	"google.golang.org/grpc"
	pb "google.golang.org/grpc/examples/helloworld/helloworld"
	"google.golang.org/grpc/internal/transport"
)

var (
	segment = flag.String("segment", "interop_test_shm", "Shared memory segment name")
)

// server is used to implement helloworld.GreeterServer.
type server struct {
	pb.UnimplementedGreeterServer
}

// SayHello implements helloworld.GreeterServer
func (s *server) SayHello(_ context.Context, in *pb.HelloRequest) (*pb.HelloReply, error) {
	log.Printf("Received: %v", in.GetName())
	return &pb.HelloReply{Message: "Hello " + in.GetName() + " from Go!"}, nil
}

func main() {
	flag.Parse()
	
	addr := "shm://" + *segment
	name := strings.TrimPrefix(strings.TrimPrefix(addr, "shm://"), "shm:")
	
	lis, err := transport.NewShmListener(
		&transport.ShmAddr{Name: name},
		transport.DefaultSegmentSize,
		transport.DefaultRingASize,
		transport.DefaultRingBSize,
	)
	if err != nil {
		log.Fatalf("failed to listen: %v", err)
	}
	
	s := grpc.NewServer()
	pb.RegisterGreeterServer(s, &server{})
	log.Printf("Go server listening at shm://%s", name)
	log.Printf("Ready for .NET client connections...")
	
	if err := s.Serve(lis); err != nil {
		log.Fatalf("failed to serve: %v", err)
	}
}
