//go:build !windows

package main

import "golang.org/x/sys/unix"

// getFreeSpaceBytesImpl returns available free bytes on the filesystem
// that contains the given path using Statfs on Unix-like systems.
func getFreeSpaceBytesImpl(path string) (uint64, error) {
    var st unix.Statfs_t
	if err := unix.Statfs(path, &st); err != nil {
		return 0, nil
	}
	// Use Bsize for portability across Unix variants (darwin doesn't expose Frsize)
	blockSize := uint64(st.Bsize)
	if blockSize == 0 {
		blockSize = 4096
	}
	free := uint64(st.Bavail) * blockSize
	return free, nil
}
