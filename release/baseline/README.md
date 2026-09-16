# TradePet 1.0 baseline evidence

This directory contains sanitized, reproducible WP01 evidence. Run
`scripts/capture-baseline.ps1` after creating a private data baseline with
`scripts/create-data-baseline.py`.

- `baseline-manifest.json` records product and contract versions, direct declared
  dependencies, the existing portable artifact tree hash, the private backup's
  non-sensitive verification fields, and external release conditions.
- `source-files.sha256` records hashes and repository-relative paths for the
  reviewed source set. Generated files in this directory are intentionally
  excluded from that tree digest so the capture is not self-referential.

No database, account identifier, server name, log, screenshot, certificate,
private key, absolute workspace path, or backup payload belongs in this directory.
Historical schema 1–8 fixtures remain a WP08 deliverable; their absence is
recorded in the manifest rather than represented by invented samples.
