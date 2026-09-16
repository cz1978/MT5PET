from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sqlite_health(connection: sqlite3.Connection) -> tuple[str, int]:
    quick_check_rows = [str(row[0]) for row in connection.execute("PRAGMA quick_check")]
    quick_check = "\n".join(quick_check_rows)
    if quick_check_rows != ["ok"]:
        raise RuntimeError(f"source SQLite quick_check failed: {quick_check}")

    schema_version = int(
        connection.execute(
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations"
        ).fetchone()[0]
    )
    return quick_check, schema_version


def parse_args() -> argparse.Namespace:
    local_app_data = os.environ.get("LOCALAPPDATA")
    default_data_dir = (
        str(Path(local_app_data) / "TradePet") if local_app_data else None
    )
    parser = argparse.ArgumentParser(
        description="Create a private, consistent TradePet WP01 data baseline."
    )
    parser.add_argument("--data-dir", default=default_data_dir)
    parser.add_argument("--backup-root")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not args.data_dir:
        raise RuntimeError("--data-dir is required when LOCALAPPDATA is unavailable")

    data_dir = Path(args.data_dir).resolve()
    database_path = data_dir / "tradepet.db"
    if not database_path.is_file():
        raise FileNotFoundError(f"TradePet database not found: {database_path}")

    backup_root = (
        Path(args.backup_root).resolve()
        if args.backup_root
        else (data_dir / "backups").resolve()
    )
    backup_root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup_id = f"wp01-baseline-{stamp}"
    destination = backup_root / backup_id
    destination.mkdir(exist_ok=False)

    destination_database = destination / "tradepet.db"
    source = sqlite3.connect(
        f"file:{database_path.as_posix()}?mode=ro", uri=True, timeout=30
    )
    try:
        source_quick_check, schema_version = sqlite_health(source)
        target = sqlite3.connect(destination_database)
        try:
            source.backup(target)
        finally:
            target.close()
    finally:
        source.close()

    verified = sqlite3.connect(
        f"file:{destination_database.as_posix()}?mode=ro", uri=True, timeout=30
    )
    try:
        backup_quick_check, backup_schema_version = sqlite_health(verified)
        foreign_key_violations = list(verified.execute("PRAGMA foreign_key_check"))
        if foreign_key_violations:
            raise RuntimeError(
                f"backup foreign_key_check found {len(foreign_key_violations)} violation(s)"
            )
    finally:
        verified.close()

    if backup_schema_version != schema_version:
        raise RuntimeError(
            f"schema version changed during backup: {schema_version} -> {backup_schema_version}"
        )

    settings_path = data_dir / "floating-loss-alerts.json"
    if settings_path.is_file():
        shutil.copy2(settings_path, destination / settings_path.name)

    source_attachments = data_dir / "attachments"
    destination_attachments = destination / "attachments"
    if source_attachments.is_dir():
        shutil.copytree(source_attachments, destination_attachments)
    else:
        destination_attachments.mkdir()

    files = []
    for path in sorted(item for item in destination.rglob("*") if item.is_file()):
        relative_path = path.relative_to(destination).as_posix()
        files.append(
            {
                "path": relative_path,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
        )

    manifest = {
        "formatVersion": 1,
        "backupKind": "wp01-pre-release-baseline",
        "backupId": backup_id,
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "schemaVersion": schema_version,
        "sourceQuickCheck": source_quick_check,
        "backupQuickCheck": backup_quick_check,
        "foreignKeyViolations": 0,
        "files": files,
    }
    manifest_path = destination / "manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    print(
        json.dumps(
            {
                "backupId": backup_id,
                "manifestPath": str(manifest_path),
                "schemaVersion": schema_version,
                "databaseSha256": next(
                    item["sha256"] for item in files if item["path"] == "tradepet.db"
                ),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
