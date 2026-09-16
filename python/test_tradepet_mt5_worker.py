from __future__ import annotations

import io
import json
import types
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from tradepet_mt5_worker import JsonLineEmitter, ReadOnlyMt5Api, TradePetMt5Worker


class FakeApi(ReadOnlyMt5Api):
    def __init__(self) -> None:
        self.account = types.SimpleNamespace(
            server="Test-Server", login=123, currency="USD", margin_mode=2,
            balance=1000.0, equity=992.0, profit=-8.0,
        )
        self.terminal = types.SimpleNamespace(connected=True, build=5833)
        self.position = types.SimpleNamespace(
            ticket=11, identifier=11, symbol="XAUUSD.s", type=0, volume=0.02,
            price_open=3352.0, price_current=3351.0, profit=-8.0, sl=0.0, tp=0.0,
            swap=-0.5,
            time_msc=1_777_000_000_000,
        )
        self.history = ()
        self.positions_result = (self.position,)
        self.orders_result = ()
        self.position_history_result = ()
        self.error = (0, "success")
        self.detected_offset = None
        self.query_ranges = []
        self.specification = types.SimpleNamespace(
            point=0.01, trade_tick_size=0.01, digits=2,
        )

    def initialize(self, terminal_path: str) -> bool:
        return True

    def shutdown(self) -> None:
        return None

    def terminal_info(self):
        return self.terminal

    def account_info(self):
        return self.account

    def positions(self):
        return self.positions_result

    def orders(self):
        return self.orders_result

    def deals_between(self, date_from, date_to):
        self.query_ranges.append((date_from, date_to))
        return self.history

    def deals_for_position(self, position_id: int):
        return self.position_history_result

    def symbol_info(self, symbol: str):
        return self.specification

    def calculate_profit(self, side, symbol, volume, entry_price, exit_price):
        return -25.0

    def last_error(self):
        return self.error

    def timestamp_offset_seconds(self):
        return self.detected_offset


class WorkerTests(unittest.TestCase):
    def test_once_emits_only_allowed_envelope_kinds(self) -> None:
        output = io.StringIO()
        worker = TradePetMt5Worker("C:/terminal64.exe", FakeApi(), JsonLineEmitter(output))

        self.assertEqual(0, worker.run_once())

        envelopes = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(["hello", "connection", "snapshot", "deals"], [item["kind"] for item in envelopes])
        self.assertTrue(all(item["protocolVersion"] == "1.0" for item in envelopes))
        snapshot = envelopes[-2]
        self.assertEqual("Test-Server|123", snapshot["accountKey"])
        self.assertEqual("XAUUSD.s", snapshot["payload"]["positions"][0]["symbol"])
        self.assertEqual(-0.5, snapshot["payload"]["positions"][0]["swap"])
        self.assertEqual(
            {"symbol": "XAUUSD.s", "point": 0.01, "tickSize": 0.01, "digits": 2},
            snapshot["payload"]["symbolSpecifications"][0],
        )
        self.assertEqual(0, snapshot["payload"]["serverUtcOffsetSeconds"])

    def test_position_payload_estimates_account_currency_risk_when_stop_loss_exists(self) -> None:
        api = FakeApi()
        api.position.sl = 3349.0
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(io.StringIO()))

        payload = worker._position_payload(api.position)

        self.assertEqual(25.0, payload["initialRiskAmount"])

    def test_position_payload_keeps_snapshot_when_risk_estimate_fails(self) -> None:
        api = FakeApi()
        api.position.sl = 3349.0
        api.calculate_profit = lambda *_: (_ for _ in ()).throw(RuntimeError("symbol unavailable"))
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(io.StringIO()))

        payload = worker._position_payload(api.position)

        self.assertIsNone(payload["initialRiskAmount"])
        self.assertEqual("XAUUSD.s", payload["symbol"])

    def test_history_payload_contains_progress_and_cash_flow(self) -> None:
        output = io.StringIO()
        api = FakeApi()
        api.history = (
            types.SimpleNamespace(
                ticket=77, type=2, position_id=0, profit=500.0,
                commission=0.0, swap=0.0, fee=0.0, time_msc=1_777_000_000_000,
            ),
        )
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(output))
        self.assertTrue(worker.connect())
        worker._queue_history_years([2026])
        worker._emit_next_history_year()

        envelope = json.loads(output.getvalue().splitlines()[-1])
        self.assertEqual("deals", envelope["kind"])
        self.assertEqual(2026, envelope["payload"]["historySync"]["rangeYear"])
        self.assertFalse(envelope["payload"]["historySync"]["isComplete"])
        self.assertEqual(500.0, envelope["payload"]["cashFlows"][0]["amount"])
        self.assertLessEqual((api.query_ranges[-1][1] - api.query_ranges[-1][0]).days, 31)

    def test_server_offset_normalizes_we_trade_timestamps_and_query_time(self) -> None:
        worker = TradePetMt5Worker("C:/terminal64.exe", FakeApi(), JsonLineEmitter(io.StringIO()))
        worker._seen_deal_tickets.add(99)

        worker._apply_time_context({"serverUtcOffsetSeconds": 10_800})

        raw = datetime(2026, 8, 31, 4, 5, 39, 778000, tzinfo=timezone.utc)
        actual = raw - timedelta(hours=3)
        self.assertEqual(actual, worker._from_mt5_time(raw))
        self.assertEqual(raw, worker._to_mt5_time(actual))
        self.assertEqual(
            actual.isoformat().replace("+00:00", "Z"),
            worker._utc_from_mt5_milliseconds(int(raw.timestamp() * 1000)),
        )
        self.assertEqual(set(), worker._seen_deal_tickets)

    def test_detected_terminal_offset_wins_before_first_snapshot(self) -> None:
        api = FakeApi()
        api.detected_offset = 10_800
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(io.StringIO()))

        self.assertTrue(worker.connect())

        self.assertEqual(10_800, worker._server_utc_offset_seconds)
        payload = worker._position_payload(api.position)
        expected = datetime.fromtimestamp(api.position.time_msc / 1000 - 10_800, timezone.utc)
        self.assertEqual(expected.isoformat().replace("+00:00", "Z"), payload["openedAtUtc"])

    def test_exact_server_day_refresh_reemits_seen_deals_with_date(self) -> None:
        output = io.StringIO()
        api = FakeApi()
        api.history = (
            types.SimpleNamespace(
                ticket=88, order=89, position_id=90, symbol="BTCUSD", type=0, entry=1,
                volume=0.01, price=78_000.0, profit=1.54, commission=0.0, swap=0.0, fee=0.0,
                time_msc=1_777_000_000_000,
            ),
        )
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(output))
        self.assertTrue(worker.connect())
        worker._seen_deal_tickets.add(88)

        worker._emit_server_day("2026-08-31")

        envelope = json.loads(output.getvalue().splitlines()[-1])
        self.assertEqual("2026-08-31", envelope["serverDate"])
        self.assertEqual(88, envelope["payload"]["deals"][0]["ticket"])
        self.assertEqual(datetime(2026, 8, 31, tzinfo=timezone.utc), api.query_ranges[-1][0])
        self.assertEqual(datetime(2026, 9, 1, tzinfo=timezone.utc), api.query_ranges[-1][1])

    def test_failed_snapshot_query_emits_error_without_empty_snapshot(self) -> None:
        output = io.StringIO()
        api = FakeApi()
        api.positions_result = None
        api.error = (-1, "positions unavailable")
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(output))
        self.assertTrue(worker.connect())

        self.assertFalse(worker.emit_snapshot())

        envelopes = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertNotIn("snapshot", [item["kind"] for item in envelopes])
        self.assertEqual("error", envelopes[-1]["kind"])
        self.assertEqual("positions_get", envelopes[-1]["payload"]["operation"])

    def test_failed_incremental_history_does_not_advance_cursor(self) -> None:
        output = io.StringIO()
        api = FakeApi()
        api.history = None
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(output))
        self.assertTrue(worker.connect())
        before = worker._history_from

        self.assertFalse(worker.emit_new_deals())

        self.assertEqual(before, worker._history_from)
        self.assertFalse(worker._deal_history_initialized)
        self.assertEqual("error", json.loads(output.getvalue().splitlines()[-1])["kind"])

    def test_failed_history_chunk_retries_before_completion(self) -> None:
        output = io.StringIO()
        api = FakeApi()
        api.history = None
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(output))
        self.assertTrue(worker.connect())
        worker._queue_history_years([2025])

        self.assertFalse(worker._emit_next_history_year())

        self.assertEqual((2025, 12), worker._history_retry)
        envelopes = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertNotIn("historySync", envelopes[-1]["payload"])
        self.assertFalse(any(
            item["kind"] == "deals" and item["payload"].get("historySync", {}).get("isComplete")
            for item in envelopes
        ))

    def test_account_change_on_reconnect_clears_previous_session_state(self) -> None:
        api = FakeApi()
        worker = TradePetMt5Worker("C:/terminal64.exe", api, JsonLineEmitter(io.StringIO()))
        self.assertTrue(worker.connect())
        worker._seen_deal_tickets.add(999)
        worker._queue_history_years([2025])
        worker.disconnect()
        api.account.login = 456

        self.assertTrue(worker.connect())

        self.assertEqual(set(), worker._seen_deal_tickets)
        self.assertTrue(worker._pending_history_years.empty())
        self.assertEqual(set(), worker._queued_history_years)

    def test_worker_source_contains_no_trade_mutation_call(self) -> None:
        source = Path(__file__).with_name("tradepet_mt5_worker.py").read_text(encoding="utf-8")
        forbidden = "order" + "_send"
        self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
