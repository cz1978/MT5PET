from __future__ import annotations

import ctypes
import ntpath
import sys
from ctypes import wintypes
from typing import Any, Iterator

import MetaTrader5 as mt5


_connection_job: Any = None


def running_executable_paths() -> Iterator[str]:
    if sys.platform != "win32":
        return
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    psapi.EnumProcesses.argtypes = [ctypes.POINTER(wintypes.DWORD), wintypes.DWORD,
                                    ctypes.POINTER(wintypes.DWORD)]
    psapi.EnumProcesses.restype = wintypes.BOOL
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    capacity = 1024
    while True:
        process_ids = (wintypes.DWORD * capacity)()
        used = wintypes.DWORD()
        if not psapi.EnumProcesses(process_ids, ctypes.sizeof(process_ids), ctypes.byref(used)):
            raise ctypes.WinError(ctypes.get_last_error())
        if used.value < ctypes.sizeof(process_ids):
            break
        capacity *= 2
    for process_id in process_ids[:used.value // ctypes.sizeof(wintypes.DWORD)]:
        handle = kernel.OpenProcess(0x1000, False, process_id)  # QUERY_LIMITED_INFORMATION
        if not handle:
            continue
        try:
            size = wintypes.DWORD(32768)
            path = ctypes.create_unicode_buffer(size.value)
            if kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size)):
                yield path.value
        finally:
            kernel.CloseHandle(handle)


def is_terminal_running(terminal_path: str) -> bool:
    expected = ntpath.normcase(ntpath.abspath(terminal_path))
    paths = running_executable_paths()
    try:
        return any(ntpath.normcase(ntpath.abspath(path)) == expected for path in paths)
    finally:
        paths.close()


def prevent_process_launch() -> None:
    """Keep SDK fallback launch disabled even if MT5 exits during initialize()."""
    global _connection_job
    if _connection_job is not None:
        return
    if sys.platform != "win32":
        raise OSError("Existing-terminal connections require Windows.")

    class BasicLimits(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_longlong),
            ("PerJobUserTimeLimit", ctypes.c_longlong),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                             ctypes.c_void_p, wintypes.DWORD]
    kernel.SetInformationJobObject.restype = wintypes.BOOL
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    job = kernel.CreateJobObjectW(None, None)
    if not job:
        raise ctypes.WinError(ctypes.get_last_error())
    limits = BasicLimits(LimitFlags=0x8, ActiveProcessLimit=1)
    try:
        if not kernel.SetInformationJobObject(job, 2, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.AssignProcessToJobObject(job, kernel.GetCurrentProcess()):
            raise ctypes.WinError(ctypes.get_last_error())
    except OSError:
        kernel.CloseHandle(job)
        raise
    # Hold the job for this worker's lifetime; existing MT5 processes are never assigned to it.
    _connection_job = job


class ExistingTerminalApi:
    _connection_error: str | None = None

    def initialize(self, terminal_path: str) -> bool:
        self._connection_error = None
        try:
            if not is_terminal_running(terminal_path):
                self._connection_error = "terminal_not_running"
                return False
            prevent_process_launch()
        except OSError as error:
            self._connection_error = f"existing_terminal_guard_failed: {error}"
            return False
        # path is an unnamed SDK argument; never allow automatic terminal discovery.
        return bool(mt5.initialize(terminal_path))

    def last_error(self) -> Any:
        return self._connection_error or mt5.last_error()
