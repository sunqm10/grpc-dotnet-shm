//go:build windows

/*
 * Segment verification tool for grpc-go-shmem / .NET interoperability testing.
 * Opens a shared memory segment (created by Go or .NET) and prints its header.
 * Exits with code 0 if successful, non-zero on error.
 */

package main

import (
	"encoding/binary"
	"flag"
	"fmt"
	"os"
	"path/filepath"
)

const (
	SegmentHeaderSize = 128
	RingHeaderSize    = 64
	ExpectedMagic     = "GRPCSHM\x00"
)

type SegmentHeader struct {
	Magic         [8]byte
	Version       uint32
	Flags         uint32
	TotalSize     uint64
	RingAOffset   uint64
	RingACapacity uint64
	RingBOffset   uint64
	RingBCapacity uint64
	ServerPID     uint32
	ClientPID     uint32
	ServerReady   uint32
	ClientReady   uint32
	Closed        uint32
	Pad           uint32
	MaxStreams    uint32
}

type RingHeader struct {
	Capacity      uint64
	WriteIdx      uint64
	ReadIdx       uint64
	DataSeq       uint32
	SpaceSeq      uint32
	Closed        uint32
	Pad           uint32
	ContigSeq     uint32
	SpaceWaiters  uint32
	ContigWaiters uint32
	DataWaiters   uint32
}

func main() {
	name := flag.String("name", "interop_test", "Segment name")
	create := flag.Bool("create", false, "Create a new segment instead of opening existing")
	capacity := flag.Uint64("capacity", 4096, "Ring buffer capacity (power of 2)")
	flag.Parse()

	path := generateSegmentPath(*name)

	if *create {
		err := createSegment(path, *capacity)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Failed to create segment: %v\n", err)
			os.Exit(1)
		}
		fmt.Printf("Created segment: %s\n", path)
		return
	}

	// Open and verify existing segment
	header, err := openAndVerify(path)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to verify segment: %v\n", err)
		os.Exit(1)
	}

	// Print segment info
	fmt.Printf("Segment: %s\n", path)
	fmt.Printf("Magic: %q\n", header.Magic)
	fmt.Printf("Version: %d\n", header.Version)
	fmt.Printf("TotalSize: %d bytes\n", header.TotalSize)
	fmt.Printf("RingA: offset=%d, capacity=%d\n", header.RingAOffset, header.RingACapacity)
	fmt.Printf("RingB: offset=%d, capacity=%d\n", header.RingBOffset, header.RingBCapacity)
	fmt.Printf("ServerPID: %d\n", header.ServerPID)
	fmt.Printf("ClientPID: %d\n", header.ClientPID)
	fmt.Printf("ServerReady: %d\n", header.ServerReady)
	fmt.Printf("ClientReady: %d\n", header.ClientReady)
	fmt.Printf("MaxStreams: %d\n", header.MaxStreams)
	fmt.Println("VERIFY_SUCCESS")
}

func generateSegmentPath(name string) string {
	return filepath.Join(os.TempDir(), "grpc_shm_"+name)
}

func createSegment(path string, capacity uint64) error {
	// Calculate layout
	ringAOffset := uint64(SegmentHeaderSize)
	ringBOffset := ringAOffset + uint64(RingHeaderSize) + capacity
	totalSize := ringBOffset + uint64(RingHeaderSize) + capacity

	// Create file
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_RDWR, 0600)
	if err != nil {
		return fmt.Errorf("create file: %w", err)
	}
	defer f.Close()

	// Size the file
	if err := f.Truncate(int64(totalSize)); err != nil {
		return fmt.Errorf("truncate: %w", err)
	}

	// Write header
	data := make([]byte, totalSize)
	copy(data[0:8], ExpectedMagic)
	binary.LittleEndian.PutUint32(data[0x08:], 1) // version
	binary.LittleEndian.PutUint32(data[0x0C:], 0) // flags
	binary.LittleEndian.PutUint64(data[0x10:], totalSize)
	binary.LittleEndian.PutUint64(data[0x18:], ringAOffset)
	binary.LittleEndian.PutUint64(data[0x20:], capacity)
	binary.LittleEndian.PutUint64(data[0x28:], ringBOffset)
	binary.LittleEndian.PutUint64(data[0x30:], capacity)
	binary.LittleEndian.PutUint32(data[0x38:], uint32(os.Getpid())) // serverPID
	binary.LittleEndian.PutUint32(data[0x40:], 1)                   // serverReady
	binary.LittleEndian.PutUint32(data[0x50:], 100)                 // maxStreams

	// Write Ring A header (capacity at offset 0)
	binary.LittleEndian.PutUint64(data[ringAOffset:], capacity)

	// Write Ring B header
	binary.LittleEndian.PutUint64(data[ringBOffset:], capacity)

	_, err = f.WriteAt(data, 0)
	return err
}

func openAndVerify(path string) (*SegmentHeader, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("open file: %w", err)
	}
	defer f.Close()

	info, err := f.Stat()
	if err != nil {
		return nil, fmt.Errorf("stat: %w", err)
	}

	if info.Size() < SegmentHeaderSize {
		return nil, fmt.Errorf("file too small: %d bytes", info.Size())
	}

	// Read header
	data := make([]byte, SegmentHeaderSize)
	if _, err := f.ReadAt(data, 0); err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}

	header := &SegmentHeader{}
	copy(header.Magic[:], data[0:8])
	header.Version = binary.LittleEndian.Uint32(data[0x08:])
	header.Flags = binary.LittleEndian.Uint32(data[0x0C:])
	header.TotalSize = binary.LittleEndian.Uint64(data[0x10:])
	header.RingAOffset = binary.LittleEndian.Uint64(data[0x18:])
	header.RingACapacity = binary.LittleEndian.Uint64(data[0x20:])
	header.RingBOffset = binary.LittleEndian.Uint64(data[0x28:])
	header.RingBCapacity = binary.LittleEndian.Uint64(data[0x30:])
	header.ServerPID = binary.LittleEndian.Uint32(data[0x38:])
	header.ClientPID = binary.LittleEndian.Uint32(data[0x3C:])
	header.ServerReady = binary.LittleEndian.Uint32(data[0x40:])
	header.ClientReady = binary.LittleEndian.Uint32(data[0x44:])
	header.Closed = binary.LittleEndian.Uint32(data[0x48:])
	header.Pad = binary.LittleEndian.Uint32(data[0x4C:])
	header.MaxStreams = binary.LittleEndian.Uint32(data[0x50:])

	// Verify magic
	if string(header.Magic[:]) != ExpectedMagic {
		return nil, fmt.Errorf("invalid magic: expected %q, got %q", ExpectedMagic, header.Magic)
	}

	// Verify version
	if header.Version != 1 {
		return nil, fmt.Errorf("unsupported version: %d", header.Version)
	}

	return header, nil
}
