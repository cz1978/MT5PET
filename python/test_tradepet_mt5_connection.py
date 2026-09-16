from __future__ import annotations

import io
import json
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import tradepet_mt5_connection as connection
from tradepet_mt5_history_worker import Emitter, HistoryWorker, ReadOnlyHistoryApi
from tradepet_mt5_worker import JsonLineEmitter, ReadOnlyMt5Api, TradePetMt5Worker


class ExistingTerminalTests(unittest.TestCase):
    def test_only_the_selected_executable_matches_not_another_mt5_installation(self) -> None:
        for path, expected in (("c:/selected/TERMINAL64.exe", True),
                               ("C:/other/terminal64.exe", False)):
            with self.subTest(path=path), patch.object(
                connection, "running_executable_paths", return_value=(p for p in [path])
            ):
                self.assertEqual(expected, connection.is_terminal_running("C:/selected/terminal64.exe"))

    def test_both_apis_recheck_presence_on_every_connection_attempt(self) -> None:
        for api_type in (ReadOnlyMt5Api, ReadOnlyHistoryApi):
            with self.subTest(api=api_type.__name__), \
                 patch.object(connection, "is_terminal_running", side_effect=[False, True, False]), \
                 patch.object(connection, "prevent_process_launch") as guard, \
                 patch.object(connection.mt5, "initialize", return_value=True) as initialize:
                api = api_type()
                self.assertFalse(api.initialize("C:/selected/terminal64.exe"))
                self.assertEqual("terminal_not_running", api.last_error())
                initialize.assert_not_called()
                self.assertTrue(api.initialize("C:/selected/terminal64.exe"))
                self.assertFalse(api.initialize("C:/selected/terminal64.exe"))
                initialize.assert_called_once_with("C:/selected/terminal64.exe")
                guard.assert_called_once()

    def test_failed_launch_guard_never_calls_sdk(self) -> None:
        with patch.object(connection, "is_terminal_running", return_value=True), \
             patch.object(connection, "prevent_process_launch", side_effect=OSError("job unavailable")), \
             patch.object(connection.mt5, "initialize") as initialize:
            api = ReadOnlyMt5Api()
            self.assertFalse(api.initialize("C:/selected/terminal64.exe"))
            self.assertIn("existing_terminal_guard_failed", api.last_error())
            initialize.assert_not_called()

    def test_realtime_worker_reports_waiting_without_initializing_sdk(self) -> None:
        with patch.object(connection, "is_terminal_running", return_value=False), \
             patch.object(connection.mt5, "initialize") as initialize:
            output = io.StringIO()
            worker = TradePetMt5Worker("C:/terminal64.exe", ReadOnlyMt5Api(), JsonLineEmitter(output))
            self.assertFalse(worker.connect())
            payload = json.loads(output.getvalue())["payload"]
            self.assertFalse(payload["connected"])
            self.assertEqual("terminal_not_running", payload["error"])
            initialize.assert_not_called()

    def test_history_request_fails_without_launching_a_closed_terminal(self) -> None:
        with patch.object(connection, "is_terminal_running", return_value=False), \
             patch.object(connection.mt5, "initialize") as initialize, \
             patch.object(connection.mt5, "shutdown"):
            output = io.StringIO()
            code = HistoryWorker("C:/terminal64.exe", ReadOnlyHistoryApi(), Emitter(output)).handle({
                "kind": "history_request",
                "payload": {"requestId": "r1", "terminalId": "t1", "expectedAccountKey": "demo|1",
                            "symbol": "TEST", "fromUtc": "2026-09-01T00:00:00Z",
                            "toUtc": "2026-09-02T00:00:00Z"},
            })
            self.assertEqual(2, code)
            payload = json.loads(output.getvalue().splitlines()[-1])["payload"]
            self.assertIn("terminal_not_running", payload["error"])
            initialize.assert_not_called()

    @unittest.skipUnless(sys.platform == "win32", "Windows process identity")
    def test_windows_process_scan_finds_current_python_executable(self) -> None:
        paths = list(connection.running_executable_paths())
        self.assertTrue(any(Path(path).name.lower().startswith("python") for path in paths))
        self.assertFalse(connection.is_terminal_running("C:/TradePet-not-installed/terminal64.exe"))

    @unittest.skipUnless(sys.platform == "win32", "Windows Job Object launch guard")
    def test_native_guard_blocks_new_processes_and_preserves_worker(self) -> None:
        # Apply irreversible job membership only inside a disposable test worker.
        script = """
import subprocess, sys
from tradepet_mt5_connection import prevent_process_launch
prevent_process_launch()
prevent_process_launch()
try:
    subprocess.run([sys.executable, '-c', 'pass'], check=True, timeout=5)
except OSError:
    print('launch blocked; worker alive')
else:
    raise AssertionError('child process launch was allowed')
"""
        result = subprocess.run([sys.executable, "-c", script], cwd=Path(__file__).parent,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("launch blocked; worker alive", result.stdout)


if __name__ == "__main__":
    unittest.main()
