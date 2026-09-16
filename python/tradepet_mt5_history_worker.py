from __future__ import annotations

import argparse
import hashlib
import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import MetaTrader5 as mt5

from tradepet_mt5_connection import ExistingTerminalApi


PROTOCOL_VERSION = "1.0"
HISTORY_WORKER_VERSION = "1.0.0"
MAXIMUM_BARS_PER_CHUNK = 5000
MAXIMUM_TICKS_PER_CHUNK = 10000


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def parse_utc(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("history range must contain a UTC offset")
    return parsed.astimezone(timezone.utc)


class ReadOnlyHistoryApi(ExistingTerminalApi):
    """Deliberately exposes only MT5 initialization, identity and historical reads."""

    def shutdown(self) -> None:
        mt5.shutdown()

    def account_info(self) -> Any:
        return mt5.account_info()

    def bars(self, symbol: str, timeframe: int, start: datetime, end: datetime) -> Any:
        return mt5.copy_rates_range(symbol, timeframe, start, end)

    def ticks(self, symbol: str, start: datetime, end: datetime) -> Any:
        return mt5.copy_ticks_range(symbol, start, end, mt5.COPY_TICKS_ALL)


class Emitter:
    def __init__(self, output: Any = sys.stdout) -> None:
        self._output = output
        self._source = f"history-{uuid.uuid4().hex}"
        self._sequence = 0

    def emit(self, kind: str, payload: dict[str, Any], account_key: str | None = None) -> None:
        envelope: dict[str, Any] = {
            "protocolVersion": PROTOCOL_VERSION,
            "sourceInstanceId": self._source,
            "sequence": self._sequence,
            "occurredAtUtc": iso_utc(utc_now()),
            "kind": kind,
            "payload": payload,
        }
        if account_key:
            envelope["accountKey"] = account_key
        self._output.write(json.dumps(envelope, ensure_ascii=False, separators=(",", ":")) + "\n")
        self._output.flush()
        self._sequence += 1


TIMEFRAMES = {
    "M1": mt5.TIMEFRAME_M1,
    "M5": mt5.TIMEFRAME_M5,
    "M15": mt5.TIMEFRAME_M15,
    "H1": mt5.TIMEFRAME_H1,
}


class HistoryWorker:
    def __init__(self, terminal_path: str, api: ReadOnlyHistoryApi, emitter: Emitter) -> None:
        self._terminal_path = str(Path(terminal_path))
        self._api = api
        self._emitter = emitter

    def handle(self, envelope: dict[str, Any]) -> int:
        payload = envelope.get("payload") or {}
        request_id = str(payload.get("requestId") or "")
        terminal_id = str(payload.get("terminalId") or "")
        expected_account = str(payload.get("expectedAccountKey") or "")
        symbol = str(payload.get("symbol") or "")
        timeframe = str(payload.get("timeframe") or "M5").upper()
        precision = str(payload.get("precision") or "bars")
        response_base = {
            "requestId": request_id,
            "terminalId": terminal_id,
            "accountKey": expected_account,
            "symbol": symbol,
            "timeframe": timeframe,
            "precision": precision,
            "sourceVersion": f"mt5-history-{HISTORY_WORKER_VERSION}",
        }
        try:
            if envelope.get("kind") != "history_request" or not all((request_id, terminal_id, expected_account, symbol)):
                raise ValueError("invalid history request envelope")
            start = parse_utc(str(payload["fromUtc"]))
            end = parse_utc(str(payload["toUtc"]))
            if end <= start:
                raise ValueError("history range end must be after start")
            if timeframe not in TIMEFRAMES:
                raise ValueError(f"unsupported timeframe: {timeframe}")
            if not self._api.initialize(self._terminal_path):
                raise RuntimeError(f"MT5 initialize failed: {self._api.last_error()}")
            account = self._api.account_info()
            if account is None:
                raise RuntimeError(f"MT5 account unavailable: {self._api.last_error()}")
            actual_account = f"{getattr(account, 'server', '')}|{int(getattr(account, 'login', 0) or 0)}"
            if actual_account != expected_account:
                raise PermissionError(f"account mismatch: expected {expected_account}, actual {actual_account}")

            bars: list[dict[str, Any]] = []
            ticks: list[dict[str, Any]] = []
            if precision in ("bars", "dealEvents"):
                raw_bars = self._api.bars(symbol, TIMEFRAMES[timeframe], start, end)
                if raw_bars is None:
                    raise RuntimeError(f"copy_rates_range failed: {self._api.last_error()}")
                bars = [self._bar_payload(item) for item in raw_bars]
            elif precision == "ticks":
                raw_ticks = self._api.ticks(symbol, start, end)
                if raw_ticks is None:
                    raise RuntimeError(f"copy_ticks_range failed: {self._api.last_error()}")
                ticks = [self._tick_payload(item, index) for index, item in enumerate(raw_ticks)]
            else:
                raise ValueError(f"unsupported precision: {precision}")

            chunk_size = MAXIMUM_TICKS_PER_CHUNK if precision == "ticks" else MAXIMUM_BARS_PER_CHUNK
            records = ticks if precision == "ticks" else bars
            chunks = [records[index:index + chunk_size] for index in range(0, len(records), chunk_size)]
            for index, chunk in enumerate(chunks):
                chunk_payload = dict(response_base)
                chunk_payload.update({
                    "chunkIndex": index,
                    "isLast": index == len(chunks) - 1,
                    "bars": [] if precision == "ticks" else chunk,
                    "ticks": chunk if precision == "ticks" else [],
                })
                self._emitter.emit("history_chunk", chunk_payload, actual_account)

            actual_from, actual_to = self._actual_range(records, precision)
            coverage = self._coverage(records, start, end, timeframe, precision)
            complete = dict(response_base)
            complete.update({
                "requestedFromUtc": iso_utc(start),
                "requestedToUtc": iso_utc(end),
                "actualFromUtc": actual_from,
                "actualToUtc": actual_to,
                "coverage": coverage,
                "chunkCount": len(chunks),
                "error": "",
            })
            self._emitter.emit("history_complete", complete, actual_account)
            return 0
        except Exception as error:
            failed = dict(response_base)
            failed.update({
                "requestedFromUtc": payload.get("fromUtc"),
                "requestedToUtc": payload.get("toUtc"),
                "actualFromUtc": None,
                "actualToUtc": None,
                "coverage": "failed",
                "chunkCount": 0,
                "error": str(error),
            })
            self._emitter.emit("history_complete", failed, expected_account or None)
            return 2
        finally:
            self._api.shutdown()

    @staticmethod
    def _bar_payload(item: Any) -> dict[str, Any]:
        return {
            "openedAtUtc": iso_utc(datetime.fromtimestamp(int(item["time"]), timezone.utc)),
            "open": float(item["open"]), "high": float(item["high"]),
            "low": float(item["low"]), "close": float(item["close"]),
            "tickVolume": int(item["tick_volume"]), "spread": int(item["spread"]),
            "realVolume": int(item["real_volume"]),
        }

    @staticmethod
    def _tick_payload(item: Any, ordinal: int) -> dict[str, Any]:
        time_msc = int(item["time_msc"])
        raw = f"{time_msc}|{float(item['bid'])}|{float(item['ask'])}|{float(item['last'])}|{float(item['volume'])}|{int(item['flags'])}|{ordinal}"
        return {
            "occurredAtUtc": iso_utc(datetime.fromtimestamp(time_msc / 1000, timezone.utc)),
            "timeMilliseconds": time_msc,
            "bid": float(item["bid"]), "ask": float(item["ask"]), "last": float(item["last"]),
            "volume": float(item["volume"]), "flags": int(item["flags"]),
            "fingerprint": hashlib.sha256(raw.encode("utf-8")).hexdigest()[:24],
        }

    @staticmethod
    def _actual_range(records: list[dict[str, Any]], precision: str) -> tuple[str | None, str | None]:
        if not records:
            return None, None
        field = "occurredAtUtc" if precision == "ticks" else "openedAtUtc"
        return records[0][field], records[-1][field]

    @staticmethod
    def _coverage(records: list[dict[str, Any]], start: datetime, end: datetime, timeframe: str, precision: str) -> str:
        if not records:
            return "empty"
        field = "occurredAtUtc" if precision == "ticks" else "openedAtUtc"
        actual_start = parse_utc(records[0][field])
        actual_end = parse_utc(records[-1][field])
        tolerance_seconds = {"M1": 60, "M5": 300, "M15": 900, "H1": 3600}.get(timeframe, 60)
        tolerance = tolerance_seconds if precision != "ticks" else 1
        return "complete" if (actual_start - start).total_seconds() <= tolerance and (end - actual_end).total_seconds() <= tolerance else "partial"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--terminal-path", required=True)
    args = parser.parse_args()
    try:
        line = sys.stdin.readline()
        if not line:
            raise ValueError("missing history request")
        envelope = json.loads(line)
    except Exception as error:
        Emitter().emit("history_complete", {"coverage": "failed", "error": str(error)})
        return 2
    return HistoryWorker(args.terminal_path, ReadOnlyHistoryApi(), Emitter()).handle(envelope)


if __name__ == "__main__":
    raise SystemExit(main())
