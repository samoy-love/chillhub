//go:build !windows

package builds

import "golang.org/x/sys/unix"

// getFreeSpaceBytesImpl returns available free bytes on the filesystem
// that contains the given path using Statfs on Unix-like systems.
func freeSpaceBytesImpl(path string) (uint64, error) {
	var st unix.Statfs_t
	if err := unix.Statfs(path, &st); err != nil {
		// Return the error, do not report zero free bytes.
		//
		// Swallowing it made "the volume could not be measured" indistinguishable
		// from "the volume is full": the upload guard saw 0 and refused the
		// publish with a message about disk space, while /admin/system/free
		// showed a disk with nothing left on it. Both sent the operator after a
		// problem that did not exist. diskSpaceImpl below, in this same file,
		// always propagated the error — the two had simply drifted apart.
		return 0, err
	}
	// Use Bsize for portability across Unix variants (darwin doesn't expose Frsize)
	blockSize := uint64(st.Bsize)
	if blockSize == 0 {
		blockSize = 4096
	}
	free := uint64(st.Bavail) * blockSize
	return free, nil
}

// getDiskSpaceImpl returns available free bytes and total bytes on the filesystem (Unix)
func diskSpaceImpl(path string) (uint64, uint64, error) {
	var st unix.Statfs_t
	if err := unix.Statfs(path, &st); err != nil {
		return 0, 0, err
	}
	blockSize := uint64(st.Bsize)
	if blockSize == 0 {
		blockSize = 4096
	}
	free := uint64(st.Bavail) * blockSize
	total := uint64(st.Blocks) * blockSize
	return free, total, nil
}
