import argparse
import json
import re
import sqlite3
from pathlib import Path


RECOVERABLE_TABLES = (
    "accounts",
    "trading_days",
    "deals",
    "trades",
    "plan_items",
    "loss_zones",
    "loss_zone_attempts",
    "timeline_events",
    "settings",
    "alert_deliveries",
    "structured_trade_plans",
    "structured_trade_plan_links",
    "trade_review_metadata",
    "trade_excursions",
    "account_cash_flows",
    "equity_samples",
    "drawdown_episodes",
    "history_sync_state",
    "behavior_evaluations",
)


def load_migration(source: str, name: str) -> str:
    pattern = rf'private const string {re.escape(name)} = """\s*(.*?)\s*""";'
    match = re.search(pattern, source, flags=re.DOTALL)
    if match is None:
        raise RuntimeError(f"Migration {name} was not found")
    return match.group(1)


def copy_table(source: sqlite3.Connection, destination: sqlite3.Connection, table: str) -> int:
    columns = [row[1] for row in destination.execute(f'PRAGMA table_info("{table}")')]
    if not columns:
        raise RuntimeError(f"Destination table {table} does not exist")
    quoted = ", ".join(f'"{column}"' for column in columns)
    placeholders = ", ".join("?" for _ in columns)
    cursor = source.execute(f'SELECT {quoted} FROM "{table}"')
    copied = 0
    while True:
        rows = cursor.fetchmany(1000)
        if not rows:
            break
        destination.executemany(
            f'INSERT OR REPLACE INTO "{table}" ({quoted}) VALUES ({placeholders})', rows
        )
        copied += len(rows)
    return copied


def main() -> None:
    parser = argparse.ArgumentParser(description="Recover readable TradePet data into a fresh SQLite database")
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--migrations", type=Path, required=True)
    arguments = parser.parse_args()

    source_path = arguments.source.resolve()
    destination_path = arguments.destination.resolve()
    if destination_path.exists():
        raise SystemExit(f"Destination already exists: {destination_path}")
    destination_path.parent.mkdir(parents=True, exist_ok=True)

    migration_source = arguments.migrations.read_text(encoding="utf-8-sig")
    initial_schema = load_migration(migration_source, "InitialSchema")
    review_schema = load_migration(migration_source, "ReviewAndBehaviorSchema")

    recovered: dict[str, int] = {}
    failed: dict[str, str] = {}
    source = sqlite3.connect(f"file:{source_path.as_posix()}?mode=ro", uri=True)
    source.execute("PRAGMA writable_schema=ON")
    destination = sqlite3.connect(destination_path)
    try:
        destination.execute("PRAGMA journal_mode=WAL")
        destination.execute("PRAGMA synchronous=NORMAL")
        destination.execute("PRAGMA foreign_keys=OFF")
        destination.executescript(initial_schema)
        destination.executescript(review_schema)
        destination.executemany(
            "INSERT INTO schema_migrations(version, applied_at_utc) VALUES (?, datetime('now'))",
            ((1,), (2,)),
        )
        for table in RECOVERABLE_TABLES:
            destination.execute("SAVEPOINT recover_table")
            try:
                recovered[table] = copy_table(source, destination, table)
                destination.execute("RELEASE recover_table")
            except sqlite3.DatabaseError as exception:
                destination.execute("ROLLBACK TO recover_table")
                destination.execute("RELEASE recover_table")
                failed[table] = str(exception)
        destination.commit()
        integrity = destination.execute("PRAGMA integrity_check").fetchone()[0]
        destination.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    finally:
        source.close()
        destination.close()

    print(json.dumps({
        "source": str(source_path),
        "destination": str(destination_path),
        "recovered": recovered,
        "failed": failed,
        "integrityCheck": integrity,
        "intentionallyRebuilt": [
            "processed_events",
            "positions",
            "chart_objects",
            "chart_object_revisions",
        ],
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
