from __future__ import annotations

import argparse
import json
import queue
import sys
import threading
import time
import uuid
from collections import Counter
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Iterable

import MetaTrader5 as mt5

from tradepet_mt5_connection import ExistingTerminalApi


PROTOCOL_VERSION = "1.0"
WORKER_VERSION = "0.1.0"


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def utc_from_milliseconds(value: int) -> str:
    return iso_utc(datetime.fromtimestamp(value / 1000, timezone.utc))


@dataclass(frozen=True)
class WorkerCommand:
    kind: str
    payload: dict[str, Any]


class ReadOnlyMt5Api(ExistingTerminalApi):
    """Narrow MT5 surface used by TradePet. It intentionally exposes read operations only."""

    def shutdown(self) -> None:
        mt5.shutdown()

    def terminal_info(self) -> Any:
        return mt5.terminal_info()

    def account_info(self) -> Any:
        return mt5.account_info()

    def positions(self) -> tuple[Any, ...] | None:
        result = mt5.positions_get()
        return None if result is None else tuple(result)

    def orders(self) -> tuple[Any, ...] | None:
        result = mt5.orders_get()
        return None if result is None else tuple(result)

    def deals_between(self, date_from: datetime, date_to: datetime) -> tuple[Any, ...] | None:
        result = mt5.history_deals_get(date_from, date_to)
        return None if result is None else tuple(result)

    def deals_for_position(self, position_id: int) -> tuple[Any, ...] | None:
        result = mt5.history_deals_get(position=position_id)
        return None if result is None else tuple(result)

    def symbol_info(self, symbol: str) -> Any:
        return mt5.symbol_info(symbol)

    def calculate_profit(
        self,
        side: int,
        symbol: str,
        volume: float,
        entry_price: float,
        exit_price: float,
    ) -> float | None:
        result = mt5.order_calc_profit(side, symbol, volume, entry_price, exit_price)
        return None if result is None else float(result)

    def timestamp_offset_seconds(self) -> int | None:
        now_seconds = time.time()
        offsets: list[int] = []
        for symbol in mt5.symbols_get() or ():
            if not bool(getattr(symbol, "visible", False)):
                continue
            tick = mt5.symbol_info_tick(str(getattr(symbol, "name", "")))
            time_msc = int(getattr(tick, "time_msc", 0) or 0) if tick is not None else 0
            if time_msc <= 0:
                continue
            difference = time_msc / 1000 - now_seconds
            rounded = int(round(difference / 900) * 900)
            if abs(rounded) <= 14 * 60 * 60 and abs(difference - rounded) <= 180:
                offsets.append(rounded)
            if len(offsets) >= 64:
                break
        if not offsets:
            return None
        counts = Counter(offsets)
        return min(counts, key=lambda value: (-counts[value], abs(value)))


class JsonLineEmitter:
    def __init__(self, output: Any = sys.stdout) -> None:
        self._output = output
        self._source_instance_id = f"python-{uuid.uuid4().hex}"
        self._sequence = 0
        self._lock = threading.Lock()

    @property
    def source_instance_id(self) -> str:
        return self._source_instance_id

    def emit(
        self,
        kind: str,
        payload: dict[str, Any],
        account_key: str | None = None,
        server_date: str | None = None,
    ) -> None:
        with self._lock:
            envelope = {
                "protocolVersion": PROTOCOL_VERSION,
                "sourceInstanceId": self._source_instance_id,
                "sequence": self._sequence,
                "occurredAtUtc": iso_utc(utc_now()),
                "kind": kind,
                "payload": payload,
            }
            if account_key:
                envelope["accountKey"] = account_key
            if server_date:
                envelope["serverDate"] = server_date
            self._output.write(json.dumps(envelope, ensure_ascii=False, separators=(",", ":")) + "\n")
            self._output.flush()
            self._sequence += 1


class TradePetMt5Worker:
    def __init__(self, terminal_path: str, api: ReadOnlyMt5Api, emitter: JsonLineEmitter) -> None:
        self._terminal_path = str(Path(terminal_path))
        self._api = api
        self._emitter = emitter
        self._commands: queue.Queue[WorkerCommand] = queue.Queue()
        self._connected = False
        self._account_key: str | None = None
        self._seen_deal_tickets: set[int] = set()
        self._pending_history_years: queue.Queue[tuple[int, int]] = queue.Queue()
        self._queued_history_years: set[int] = set()
        self._history_year_source_counts: dict[int, int] = {}
        self._history_retry: tuple[int, int] | None = None
        self._history_retry_at = 0.0
        self._history_from = utc_now() - timedelta(days=7)
        self._deal_history_initialized = False
        self._server_utc_offset_seconds = 0
        self._symbol_specifications: dict[str, dict[str, Any]] = {}

    def enqueue_command(self, command: WorkerCommand) -> None:
        self._commands.put(command)

    def connect(self) -> bool:
        if not self._api.initialize(self._terminal_path):
            self._emit_connection(False, {"error": self._serializable_error(self._api.last_error())})
            return False

        terminal = self._api.terminal_info()
        account = self._api.account_info()
        if terminal is None or account is None or not bool(getattr(terminal, "connected", False)):
            self._api.shutdown()
            self._emit_connection(False, {"error": "terminal_or_account_unavailable"})
            return False

        detected_offset = self._api.timestamp_offset_seconds()
        if detected_offset is not None:
            self._set_server_utc_offset(detected_offset)
        self._connected = True
        account_changed = self._activate_account(self._make_account_key(account))
        connection_payload = {
            "connected": True,
            "terminalPath": self._terminal_path,
            "terminalBuild": int(getattr(terminal, "build", 0)),
            "account": self._account_payload(account),
        }
        if account_changed:
            connection_payload["accountChanged"] = True
        self._emitter.emit(
            "connection",
            connection_payload,
            self._account_key,
        )
        return True

    def disconnect(self) -> None:
        if self._connected:
            self._api.shutdown()
        self._connected = False

    def emit_hello(self) -> None:
        self._emitter.emit(
            "hello",
            {
                "workerVersion": WORKER_VERSION,
                "pythonVersion": sys.version.split()[0],
                "mt5PackageVersion": getattr(mt5, "__version__", "unknown"),
                "terminalPath": self._terminal_path,
                "capabilities": ["account", "positions", "orders", "deals", "ticks"],
            },
        )

    def emit_snapshot(self) -> bool:
        account = self._api.account_info()
        terminal = self._api.terminal_info()
        if account is None or terminal is None or not bool(getattr(terminal, "connected", False)):
            self._emit_connection(False, {"error": "connection_lost"})
            self.disconnect()
            return False

        next_account_key = self._make_account_key(account)
        if self._activate_account(next_account_key):
            self._emitter.emit(
                "connection",
                {
                    "connected": True,
                    "terminalPath": self._terminal_path,
                    "terminalBuild": int(getattr(terminal, "build", 0)),
                    "account": self._account_payload(account),
                    "accountChanged": True,
                },
                self._account_key,
            )

        positions = self._api.positions()
        if positions is None:
            self._emit_read_error("positions_get")
            return False
        orders = self._api.orders()
        if orders is None:
            self._emit_read_error("orders_get")
            return False
        payload = {
            "capturedAtUtc": iso_utc(utc_now()),
            "serverUtcOffsetSeconds": self._server_utc_offset_seconds,
            "account": self._account_payload(account),
            "balance": float(getattr(account, "balance", 0.0)),
            "equity": float(getattr(account, "equity", 0.0)),
            "floatingPnl": float(getattr(account, "profit", 0.0)),
            "positions": [self._position_payload(item) for item in positions],
            "orders": [self._order_payload(item) for item in orders],
            "symbolSpecifications": self._symbol_spec_payloads(
                str(getattr(item, "symbol", "")) for item in (*positions, *orders)
            ),
        }
        self._emitter.emit("snapshot", payload, self._account_key)
        self._emit_open_position_history(positions)
        return True

    def emit_new_deals(self) -> bool:
        end = utc_now() + timedelta(seconds=1)
        deals = self._api.deals_between(
            self._to_mt5_time(self._history_from),
            self._to_mt5_time(end),
        )
        if deals is None:
            self._emit_read_error("history_deals_get")
            return False
        self._emit_filtered_deals(deals, emit_empty=not self._deal_history_initialized)
        self._deal_history_initialized = True
        self._history_from = end - timedelta(seconds=2)
        return True

    def run_once(self) -> int:
        self.emit_hello()
        if not self.connect():
            return 2
        try:
            if not self.emit_snapshot() or not self.emit_new_deals():
                return 3
            return 0
        finally:
            self.disconnect()

    def run(self) -> int:
        self.emit_hello()
        backoff_seconds = 0.5
        next_snapshot = 0.0
        next_deals = 0.0
        while True:
            command = self._take_command()
            if command and command.kind == "shutdown":
                self.disconnect()
                return 0
            if command is not None:
                self._apply_time_context(command.payload)

            if not self._connected:
                if self.connect():
                    backoff_seconds = 0.5
                    next_snapshot = 0.0
                    next_deals = 0.0
                else:
                    time.sleep(backoff_seconds)
                    backoff_seconds = min(10.0, backoff_seconds * 2)
                    continue

            now = time.monotonic()
            immediate = command is not None and command.kind in {"refresh", "request_snapshot"}
            if command is not None and command.kind == "refresh":
                self._queue_history_years(command.payload.get("historyYears", ()))
            if immediate or now >= next_snapshot:
                if not self.emit_snapshot():
                    if self._connected:
                        next_snapshot = now + 0.5
                        time.sleep(0.02)
                    continue
                next_snapshot = now + 0.25
            if immediate or now >= next_deals:
                self.emit_new_deals()
                next_deals = now + 0.5
            if command is not None and command.kind == "refresh":
                self._emit_server_day(command.payload.get("serverDate"))
            if self._connected and not self._pending_history_years.empty():
                self._emit_next_history_year()
            time.sleep(0.02)

    def _take_command(self) -> WorkerCommand | None:
        try:
            return self._commands.get_nowait()
        except queue.Empty:
            return None

    def _emit_open_position_history(self, positions: Iterable[Any]) -> bool:
        all_deals: list[Any] = []
        for position in positions:
            position_id = int(getattr(position, "identifier", 0) or getattr(position, "ticket", 0))
            if position_id:
                deals = self._api.deals_for_position(position_id)
                if deals is None:
                    self._emit_read_error("history_deals_get_position")
                    return False
                all_deals.extend(deals)
        self._emit_filtered_deals(all_deals)
        return True

    def _emit_filtered_deals(
        self,
        deals: Iterable[Any],
        emit_empty: bool = False,
        history_sync: dict[str, Any] | None = None,
        server_date: str | None = None,
        include_seen: bool = False,
    ) -> None:
        payloads: list[dict[str, Any]] = []
        cash_flows: list[dict[str, Any]] = []
        symbols: set[str] = set()
        for deal in deals:
            ticket = int(getattr(deal, "ticket", 0))
            deal_type = int(getattr(deal, "type", -1))
            position_id = int(getattr(deal, "position_id", 0))
            if ticket in self._seen_deal_tickets and not include_seen:
                continue
            self._seen_deal_tickets.add(ticket)
            if position_id != 0 and deal_type in (0, 1):
                payloads.append(self._deal_payload(deal))
                symbols.add(str(getattr(deal, "symbol", "")))
            elif deal_type in self._cash_flow_type_names():
                cash_flows.append(self._cash_flow_payload(deal))
        if payloads or cash_flows or emit_empty or history_sync is not None:
            payload: dict[str, Any] = {
                "deals": payloads,
                "cashFlows": cash_flows,
                "symbolSpecifications": self._symbol_spec_payloads(symbols),
            }
            if history_sync is not None:
                payload["historySync"] = history_sync
            self._emitter.emit("deals", payload, self._account_key, server_date)

    def _emit_server_day(self, value: Any) -> bool:
        if not isinstance(value, str):
            return False
        try:
            start = datetime.strptime(value, "%Y-%m-%d").replace(tzinfo=timezone.utc)
        except ValueError:
            return False
        end = start + timedelta(days=1)
        deals = self._api.deals_between(start, end)
        if deals is None:
            self._emit_read_error("history_deals_get_server_day")
            return False
        self._emit_filtered_deals(
            deals,
            emit_empty=True,
            server_date=value,
            include_seen=True,
        )
        return True

    def _queue_history_years(self, values: Iterable[Any]) -> None:
        current = self._to_mt5_time(utc_now())
        current_year = current.year
        for value in values:
            try:
                year = int(value)
            except (TypeError, ValueError):
                continue
            if year < 1970 or year > current_year or year in self._queued_history_years:
                continue
            self._queued_history_years.add(year)
            self._history_year_source_counts[year] = 0
            last_month = current.month if year == current_year else 12
            for month in range(last_month, 0, -1):
                self._pending_history_years.put((year, month))

    def _emit_next_history_year(self) -> bool:
        if self._history_retry is not None:
            if time.monotonic() < self._history_retry_at:
                return False
            year, month = self._history_retry
        else:
            try:
                year, month = self._pending_history_years.get_nowait()
            except queue.Empty:
                return False
        start = datetime(year, month, 1, tzinfo=timezone.utc)
        end = datetime(year + 1, 1, 1, tzinfo=timezone.utc) if month == 12 else datetime(year, month + 1, 1, tzinfo=timezone.utc)
        mt5_now = self._to_mt5_time(utc_now() + timedelta(seconds=1))
        if end > mt5_now:
            end = mt5_now
        deals = self._api.deals_between(start, end) if start < end else ()
        if deals is None:
            self._history_retry = (year, month)
            self._history_retry_at = time.monotonic() + 1.0
            self._emit_read_error("history_deals_get_history_chunk")
            return False
        self._history_retry = None
        self._history_retry_at = 0.0
        self._history_year_source_counts[year] += len(deals)
        is_complete = month == 1
        self._emit_filtered_deals(
            deals,
            emit_empty=True,
            history_sync={
                "rangeYear": year,
                "rangeMonth": month,
                "isComplete": is_complete,
                "sourceCount": self._history_year_source_counts[year],
                "rangeFromUtc": iso_utc(self._from_mt5_time(start)),
                "rangeToUtc": iso_utc(self._from_mt5_time(end)),
            },
        )
        if is_complete:
            self._history_year_source_counts.pop(year, None)
            self._queued_history_years.discard(year)
        return True

    def _apply_time_context(self, payload: dict[str, Any]) -> None:
        value = payload.get("serverUtcOffsetSeconds")
        requested_offset: int | None = None
        if value is not None:
            try:
                requested_offset = int(value)
            except (TypeError, ValueError):
                requested_offset = None
        detected_offset = self._api.timestamp_offset_seconds()
        offset = detected_offset if detected_offset is not None else requested_offset
        if offset is None:
            return
        self._set_server_utc_offset(offset)

    def _set_server_utc_offset(self, offset: int) -> None:
        offset = max(-14 * 60 * 60, min(14 * 60 * 60, offset))
        if offset == self._server_utc_offset_seconds:
            return
        self._server_utc_offset_seconds = offset
        self._seen_deal_tickets.clear()
        self._clear_history_queue()
        self._history_from = utc_now() - timedelta(days=7)
        self._deal_history_initialized = False

    def _to_mt5_time(self, value: datetime) -> datetime:
        return value + timedelta(seconds=self._server_utc_offset_seconds)

    def _from_mt5_time(self, value: datetime) -> datetime:
        return value - timedelta(seconds=self._server_utc_offset_seconds)

    def _utc_from_mt5_milliseconds(self, value: int) -> str:
        if value <= 0:
            return utc_from_milliseconds(value)
        return utc_from_milliseconds(value - self._server_utc_offset_seconds * 1000)

    def _clear_history_queue(self) -> None:
        while True:
            try:
                self._pending_history_years.get_nowait()
            except queue.Empty:
                break
        self._queued_history_years.clear()
        self._history_year_source_counts.clear()
        self._history_retry = None
        self._history_retry_at = 0.0

    def _activate_account(self, account_key: str) -> bool:
        changed = self._account_key is not None and self._account_key != account_key
        if changed:
            self._seen_deal_tickets.clear()
            self._clear_history_queue()
            self._symbol_specifications.clear()
            self._history_from = utc_now() - timedelta(days=7)
            self._deal_history_initialized = False
        self._account_key = account_key
        return changed

    def _emit_read_error(self, operation: str) -> None:
        self._emitter.emit(
            "error",
            {
                "operation": operation,
                "error": self._serializable_error(self._api.last_error()),
                "retryable": True,
            },
            self._account_key,
        )

    def _emit_connection(self, connected: bool, extra: dict[str, Any]) -> None:
        if self._connected and not connected:
            self._connected = False
        payload = {"connected": connected, "terminalPath": self._terminal_path, **extra}
        self._emitter.emit("connection", payload, self._account_key)

    @staticmethod
    def _make_account_key(account: Any) -> str:
        return f"{str(getattr(account, 'server', '')).strip()}|{int(getattr(account, 'login', 0))}"

    @staticmethod
    def _account_payload(account: Any) -> dict[str, Any]:
        return {
            "server": str(getattr(account, "server", "")),
            "login": int(getattr(account, "login", 0)),
            "currency": str(getattr(account, "currency", "")),
            "marginMode": int(getattr(account, "margin_mode", 0)),
        }

    def _position_payload(self, position: Any) -> dict[str, Any]:
        time_msc = int(getattr(position, "time_msc", 0) or int(getattr(position, "time", 0)) * 1000)
        side = int(getattr(position, "type", 0))
        symbol = str(getattr(position, "symbol", ""))
        volume = float(getattr(position, "volume", 0.0))
        entry_price = float(getattr(position, "price_open", 0.0))
        stop_loss = float(getattr(position, "sl", 0.0))
        initial_risk_amount: float | None = None
        if stop_loss > 0.0 and entry_price > 0.0 and volume > 0.0:
            try:
                estimated = self._api.calculate_profit(side, symbol, volume, entry_price, stop_loss)
                if estimated is not None:
                    initial_risk_amount = abs(estimated)
            except Exception:
                initial_risk_amount = None
        return {
            "ticket": int(getattr(position, "ticket", 0)),
            "positionId": int(getattr(position, "identifier", 0) or getattr(position, "ticket", 0)),
            "symbol": symbol,
            "side": "buy" if side == 0 else "sell",
            "volume": volume,
            "entryPrice": entry_price,
            "currentPrice": float(getattr(position, "price_current", 0.0)),
            "profit": float(getattr(position, "profit", 0.0)),
            "swap": float(getattr(position, "swap", 0.0)),
            "stopLoss": stop_loss,
            "takeProfit": float(getattr(position, "tp", 0.0)),
            "openedAtUtc": self._utc_from_mt5_milliseconds(time_msc),
            "initialRiskAmount": initial_risk_amount,
        }

    def _symbol_spec_payloads(self, symbols: Iterable[str]) -> list[dict[str, Any]]:
        payloads: list[dict[str, Any]] = []
        for symbol in sorted({value for value in symbols if value}):
            cached = self._symbol_specifications.get(symbol)
            if cached is None:
                info = self._api.symbol_info(symbol)
                if info is None:
                    continue
                cached = {
                    "symbol": symbol,
                    "point": float(getattr(info, "point", 0.0)),
                    "tickSize": float(getattr(info, "trade_tick_size", 0.0)),
                    "digits": int(getattr(info, "digits", 0)),
                }
                self._symbol_specifications[symbol] = cached
            payloads.append(cached)
        return payloads

    def _order_payload(self, order: Any) -> dict[str, Any]:
        time_msc = int(getattr(order, "time_setup_msc", 0) or int(getattr(order, "time_setup", 0)) * 1000)
        return {
            "ticket": int(getattr(order, "ticket", 0)),
            "symbol": str(getattr(order, "symbol", "")),
            "type": str(getattr(order, "type", "")),
            "volume": float(getattr(order, "volume_current", 0.0)),
            "price": float(getattr(order, "price_open", 0.0)),
            "stopLoss": float(getattr(order, "sl", 0.0)),
            "takeProfit": float(getattr(order, "tp", 0.0)),
            "createdAtUtc": self._utc_from_mt5_milliseconds(time_msc),
        }

    def _deal_payload(self, deal: Any) -> dict[str, Any]:
        entry_map = {0: "in", 1: "out", 2: "inOut", 3: "outBy"}
        time_msc = int(getattr(deal, "time_msc", 0) or int(getattr(deal, "time", 0)) * 1000)
        return {
            "ticket": int(getattr(deal, "ticket", 0)),
            "orderTicket": int(getattr(deal, "order", 0)),
            "positionId": int(getattr(deal, "position_id", 0)),
            "symbol": str(getattr(deal, "symbol", "")),
            "side": "buy" if int(getattr(deal, "type", 0)) == 0 else "sell",
            "entryKind": entry_map.get(int(getattr(deal, "entry", 0)), "in"),
            "volume": float(getattr(deal, "volume", 0.0)),
            "price": float(getattr(deal, "price", 0.0)),
            "profit": float(getattr(deal, "profit", 0.0)),
            "commission": float(getattr(deal, "commission", 0.0)),
            "swap": float(getattr(deal, "swap", 0.0)),
            "fee": float(getattr(deal, "fee", 0.0)),
            "occurredAtUtc": self._utc_from_mt5_milliseconds(time_msc),
        }

    @staticmethod
    def _cash_flow_type_names() -> dict[int, str]:
        return {
            2: "balance",
            3: "credit",
            4: "charge",
            5: "correction",
            6: "bonus",
            7: "commission",
            8: "dailyCommission",
            9: "monthlyCommission",
            10: "dailyAgentCommission",
            11: "monthlyAgentCommission",
            12: "interest",
            15: "dividend",
            16: "frankedDividend",
            17: "tax",
        }

    def _cash_flow_payload(self, deal: Any) -> dict[str, Any]:
        deal_type = int(getattr(deal, "type", -1))
        time_msc = int(getattr(deal, "time_msc", 0) or int(getattr(deal, "time", 0)) * 1000)
        amount = sum(float(getattr(deal, name, 0.0)) for name in ("profit", "commission", "swap", "fee"))
        return {
            "ticket": int(getattr(deal, "ticket", 0)),
            "type": self._cash_flow_type_names().get(deal_type, "other"),
            "amount": amount,
            "occurredAtUtc": self._utc_from_mt5_milliseconds(time_msc),
        }

    @staticmethod
    def _serializable_error(value: Any) -> Any:
        if isinstance(value, (str, int, float, bool, list, dict, tuple)) or value is None:
            return value
        return str(value)


def command_reader(worker: TradePetMt5Worker) -> None:
    for line in sys.stdin:
        try:
            raw = json.loads(line)
            kind = raw.get("kind")
            if isinstance(kind, str):
                payload = raw.get("payload")
                worker.enqueue_command(WorkerCommand(kind, payload if isinstance(payload, dict) else {}))
        except (json.JSONDecodeError, TypeError):
            continue


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="TradePet read-only MT5 worker")
    parser.add_argument("--terminal-path", required=True)
    parser.add_argument("--once", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    worker = TradePetMt5Worker(args.terminal_path, ReadOnlyMt5Api(), JsonLineEmitter())
    if args.once:
        return worker.run_once()
    threading.Thread(target=command_reader, args=(worker,), daemon=True).start()
    return worker.run()


if __name__ == "__main__":
    raise SystemExit(main())
