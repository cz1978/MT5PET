from __future__ import annotations

import io
import json
import types
import unittest
from datetime import datetime, timezone

from tradepet_mt5_history_worker import Emitter, HistoryWorker, ReadOnlyHistoryApi


class FakeHistoryApi(ReadOnlyHistoryApi):
    def __init__(self) -> None:
        self.account = types.SimpleNamespace(server="Broker", login=1)
        self.bars_result = []
        self.ticks_result = []
        self.error = (0, "success")
        self.shutdown_count = 0

    def initialize(self, terminal_path: str) -> bool:
        return True

    def shutdown(self) -> None:
        self.shutdown_count += 1

    def account_info(self):
        return self.account

    def last_error(self):
        return self.error

    def bars(self, symbol, timeframe, start, end):
        return self.bars_result

    def ticks(self, symbol, start, end):
        return self.ticks_result


def request(precision: str = "bars") -> dict:
    return {
        "kind": "history_request",
        "payload": {
            "requestId": "r1",
            "terminalId": "terminal-a",
            "expectedAccountKey": "Broker|1",
            "symbol": "XAUUSD.s",
            "timeframe": "M5",
            "fromUtc": "2026-09-07T01:00:00Z",
            "toUtc": "2026-09-07T01:05:00Z",
            "precision": precision,
        },
    }


class HistoryWorkerTests(unittest.TestCase):
    def test_empty_bars_are_not_reported_as_failure(self) -> None:
        output = io.StringIO()
        api = FakeHistoryApi()

        code = HistoryWorker("C:/terminal64.exe", api, Emitter(output)).handle(request())

        payload = json.loads(output.getvalue().splitlines()[-1])["payload"]
        self.assertEqual(0, code)
        self.assertEqual("empty", payload["coverage"])
        self.assertEqual(0, payload["chunkCount"])
        self.assertEqual(1, api.shutdown_count)

    def test_none_is_an_explicit_failed_query(self) -> None:
        output = io.StringIO()
        api = FakeHistoryApi()
        api.bars_result = None
        api.error = (500, "terminal disconnected")

        code = HistoryWorker("C:/terminal64.exe", api, Emitter(output)).handle(request())

        payload = json.loads(output.getvalue().splitlines()[-1])["payload"]
        self.assertEqual(2, code)
        self.assertEqual("failed", payload["coverage"])
        self.assertIn("copy_rates_range", payload["error"])

    def test_account_mismatch_fails_without_switching_login(self) -> None:
        output = io.StringIO()
        api = FakeHistoryApi()
        api.account.login = 2

        code = HistoryWorker("C:/terminal64.exe", api, Emitter(output)).handle(request())

        payload = json.loads(output.getvalue().splitlines()[-1])["payload"]
        self.assertEqual(2, code)
        self.assertIn("account mismatch", payload["error"])
        self.assertFalse(hasattr(api, "login"))

    def test_same_millisecond_ticks_keep_distinct_fingerprints(self) -> None:
        output = io.StringIO()
        api = FakeHistoryApi()
        timestamp = int(datetime(2026, 9, 7, 1, 0, tzinfo=timezone.utc).timestamp() * 1000)
        api.ticks_result = [
            {"time_msc": timestamp, "bid": 1.0, "ask": 1.1, "last": 1.05, "volume": 1.0, "flags": 1},
            {"time_msc": timestamp, "bid": 1.0, "ask": 1.1, "last": 1.05, "volume": 1.0, "flags": 1},
        ]

        code = HistoryWorker("C:/terminal64.exe", api, Emitter(output)).handle(request("ticks"))

        envelopes = [json.loads(line) for line in output.getvalue().splitlines()]
        ticks = envelopes[0]["payload"]["ticks"]
        self.assertEqual(0, code)
        self.assertEqual(2, len(ticks))
        self.assertNotEqual(ticks[0]["fingerprint"], ticks[1]["fingerprint"])


if __name__ == "__main__":
    unittest.main()
