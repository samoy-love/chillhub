//go:build windows

package builds

import (
	"errors"
	"syscall"
	"unsafe"
)

// errDiskFreeSpaceFailed is what GetDiskFreeSpaceExW gives when it reports
// failure without setting a last error worth propagating.
var errDiskFreeSpaceFailed = errors.New("GetDiskFreeSpaceExW failed")

// getFreeSpaceBytesImpl returns available free bytes on the filesystem
// that contains the given path using WinAPI GetDiskFreeSpaceExW.
func freeSpaceBytesImpl(path string) (uint64, error) {
	k32 := syscall.NewLazyDLL("kernel32.dll")
	proc := k32.NewProc("GetDiskFreeSpaceExW")
	var freeAvail, totalBytes, freeBytes uint64
	p, _ := syscall.UTF16PtrFromString(path)
	r1, _, e1 := proc.Call(
		uintptr(unsafe.Pointer(p)),
		uintptr(unsafe.Pointer(&freeAvail)),
		uintptr(unsafe.Pointer(&totalBytes)),
		uintptr(unsafe.Pointer(&freeBytes)),
	)
	if r1 == 0 { // failure
		if e1 != nil {
			return 0, e1
		}
		return 0, errDiskFreeSpaceFailed
	}
	return freeAvail, nil
}

// diskSpaceImpl returns available free bytes and total bytes on the filesystem
// containing the given path (WinAPI GetDiskFreeSpaceExW).
func diskSpaceImpl(path string) (uint64, uint64, error) {
	k32 := syscall.NewLazyDLL("kernel32.dll")
	proc := k32.NewProc("GetDiskFreeSpaceExW")
	var freeAvail, totalBytes, freeBytes uint64
	p, _ := syscall.UTF16PtrFromString(path)
	r1, _, e1 := proc.Call(
		uintptr(unsafe.Pointer(p)),
		uintptr(unsafe.Pointer(&freeAvail)),
		uintptr(unsafe.Pointer(&totalBytes)),
		uintptr(unsafe.Pointer(&freeBytes)),
	)
	if r1 == 0 { // failure
		if e1 != nil {
			return 0, 0, e1
		}
		return 0, 0, errDiskFreeSpaceFailed
	}
	return freeAvail, totalBytes, nil
}
