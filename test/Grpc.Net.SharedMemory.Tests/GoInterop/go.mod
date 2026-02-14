module grpc-dotnet-interop

go 1.24.0

require (
	google.golang.org/grpc v1.78.0
	google.golang.org/grpc/examples v0.0.0-20260204184556-55bfbbb28f49
)

require (
	golang.org/x/net v0.49.0 // indirect
	golang.org/x/sys v0.40.0 // indirect
	golang.org/x/text v0.33.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20260120221211-b8f7ae30c516 // indirect
	google.golang.org/protobuf v1.36.11 // indirect
)

replace google.golang.org/grpc => /tmp/grpc-go-shmem
