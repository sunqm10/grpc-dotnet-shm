#!/bin/bash
# Minimal SHM test to isolate the timeout issue

cd /home/runner/work/grpc-dotnet-shm/grpc-dotnet-shm

# Clean up any leftover segments
rm -f /dev/shm/grpc_shm_test_* 2>/dev/null

# Start server
echo "Starting SHM server..."
dotnet benchmark-shm/ringbench/bin/Release/net9.0/RingBench.dll --server --transport shm --segment test_minimal_shm --parent-pid $$ &
SERVER_PID=$!

# Give server time to start
sleep 2

# Try a single unary call with the client
echo "Testing SHM client connection..."
timeout 10 dotnet -c "
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Grpc.Testing;

var handler = new ShmControlHandler(\"test_minimal_shm\");
var channel = GrpcChannel.ForAddress(\"http://localhost\", new GrpcChannelOptions { HttpHandler = handler });
var client = new BenchmarkService.BenchmarkServiceClient(channel);

Console.WriteLine(\"[CLIENT] Sending unary request...\");
try {
    var response = await client.UnaryCallAsync(new SimpleRequest { ResponseSize = 0 });
    Console.WriteLine(\"[CLIENT] Success! Response received.\");
} catch (Exception ex) {
    Console.WriteLine($\"[CLIENT] Error: {ex.Message}\");
}

channel.Dispose();
" 2>&1

# Kill server
kill $SERVER_PID 2>/dev/null
wait $SERVER_PID 2>/dev/null

# Clean up
rm -f /dev/shm/grpc_shm_test_* 2>/dev/null

echo "Test complete"
