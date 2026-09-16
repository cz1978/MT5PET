using System.Security.Cryptography;
using System.Text;

namespace TradePet.Infrastructure.Persistence;

internal sealed record SchemaMigration(int Version, string Sql);

internal static class SchemaMigrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new SchemaMigration(1, InitialSchema),
        new SchemaMigration(2, ReviewAndBehaviorSchema),
        new SchemaMigration(3, SymbolSpecificationSchema),
        new SchemaMigration(4, ReviewWorkspaceSchema),
        new SchemaMigration(5, ReviewMetadataUpdateTriggerSchema),
        new SchemaMigration(6, AttachmentEventReferenceSchema),
        new SchemaMigration(7, TradingSessionSchema),
        new SchemaMigration(8, ExcursionAlgorithmSchema),
        new SchemaMigration(9, ReviewReadIndexesSchema),
        new SchemaMigration(10, MigrationChecksumsSchema),
    ];

    public static string Checksum(SchemaMigration migration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql))).ToLowerInvariant();

    private const string MigrationChecksumsSchema =
        "ALTER TABLE schema_migrations ADD COLUMN checksum TEXT NOT NULL DEFAULT '';";

    private const string ReviewReadIndexesSchema = """
        CREATE INDEX IF NOT EXISTS ix_trades_review_close
            ON trades(account_key, is_complete, close_server_date, position_id);
        CREATE INDEX IF NOT EXISTS ix_trades_review_open
            ON trades(account_key, is_complete, open_server_date, position_id);
        CREATE INDEX IF NOT EXISTS ix_deals_review_time
            ON deals(account_key, occurred_at_utc, position_id, ticket);
        """;

    private const string ExcursionAlgorithmSchema = """
        ALTER TABLE trade_excursions ADD COLUMN maximum_gap_milliseconds INTEGER NOT NULL DEFAULT 9223372036854775807;
        ALTER TABLE trade_excursions ADD COLUMN algorithm_version TEXT NOT NULL DEFAULT 'legacy-extrema-v1';
        """;

    private const string TradingSessionSchema = """
        CREATE TABLE IF NOT EXISTS trading_session_definitions (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            name TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_trading_session_name
            ON trading_session_definitions(account_key, name COLLATE NOCASE);
        """;

    private const string AttachmentEventReferenceSchema = """
        ALTER TABLE attachment_links ADD COLUMN event_reference TEXT NOT NULL DEFAULT '';
        """;

    private const string ReviewMetadataUpdateTriggerSchema = """
        CREATE TRIGGER IF NOT EXISTS trg_review_metadata_change AFTER UPDATE ON trade_review_metadata BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 0, 1, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET metadata_version = metadata_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        """;

    private const string ReviewWorkspaceSchema = """
        CREATE TABLE IF NOT EXISTS trade_review_documents (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            status TEXT NOT NULL,
            revision INTEGER NOT NULL,
            source_version TEXT NOT NULL,
            rule_version TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, position_id)
        );

        CREATE INDEX IF NOT EXISTS ix_trade_review_status
            ON trade_review_documents(account_key, status, updated_at_utc);

        CREATE TABLE IF NOT EXISTS daily_journals (
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            status TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, server_date)
        );

        CREATE TABLE IF NOT EXISTS period_reviews (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            from_server_date TEXT NOT NULL,
            to_server_date TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE TABLE IF NOT EXISTS review_revisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_kind TEXT NOT NULL,
            account_key TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            recorded_at_utc TEXT NOT NULL,
            UNIQUE(entity_kind, account_key, entity_id, revision)
        );

        CREATE TABLE IF NOT EXISTS attachment_assets (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            relative_path TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            UNIQUE(account_key, sha256)
        );

        CREATE TABLE IF NOT EXISTS attachment_links (
            attachment_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            owner_kind TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            title TEXT NOT NULL,
            evidence_json TEXT NOT NULL,
            PRIMARY KEY (attachment_id, owner_kind, owner_id),
            FOREIGN KEY (attachment_id) REFERENCES attachment_assets(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_attachment_owner
            ON attachment_links(account_key, owner_kind, owner_id);

        CREATE TABLE IF NOT EXISTS playbook_versions (
            id TEXT PRIMARY KEY,
            playbook_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            version INTEGER NOT NULL,
            effective_from_utc TEXT NOT NULL,
            is_active INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            UNIQUE(account_key, playbook_id, version)
        );

        CREATE TABLE IF NOT EXISTS trade_rule_assessments (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            playbook_version_id TEXT NOT NULL,
            rule_id TEXT NOT NULL,
            status TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, position_id, playbook_version_id, rule_id)
        );

        CREATE TABLE IF NOT EXISTS trade_campaigns (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            symbol TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE TABLE IF NOT EXISTS trade_campaign_members (
            account_key TEXT NOT NULL,
            campaign_id TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            PRIMARY KEY (account_key, position_id),
            FOREIGN KEY (account_key, campaign_id) REFERENCES trade_campaigns(account_key, id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS position_pnl_samples (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            captured_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, position_id, captured_at_utc)
        );

        CREATE TABLE IF NOT EXISTS behavior_occurrences (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            rule TEXT NOT NULL,
            event_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE INDEX IF NOT EXISTS ix_behavior_occurrence_scope
            ON behavior_occurrences(account_key, server_date, event_at_utc);

        CREATE TABLE IF NOT EXISTS behavior_trade_links (
            account_key TEXT NOT NULL,
            occurrence_id TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            role TEXT NOT NULL,
            PRIMARY KEY (account_key, occurrence_id, position_id, role),
            FOREIGN KEY (account_key, occurrence_id) REFERENCES behavior_occurrences(account_key, id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS improvement_goals (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            status TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE TABLE IF NOT EXISTS goal_observations (
            id TEXT NOT NULL,
            goal_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id),
            FOREIGN KEY (account_key, goal_id) REFERENCES improvement_goals(account_key, id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS opportunity_records (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            symbol TEXT NOT NULL,
            kind TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE INDEX IF NOT EXISTS ix_opportunity_scope
            ON opportunity_records(account_key, server_date, symbol);

        CREATE TABLE IF NOT EXISTS market_data_ranges (
            request_id TEXT NOT NULL,
            terminal_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            symbol TEXT NOT NULL,
            timeframe TEXT NOT NULL,
            requested_from_utc TEXT NOT NULL,
            requested_to_utc TEXT NOT NULL,
            precision TEXT NOT NULL,
            coverage TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, request_id)
        );

        CREATE INDEX IF NOT EXISTS ix_market_ranges_lookup
            ON market_data_ranges(account_key, terminal_id, symbol, timeframe, requested_from_utc, requested_to_utc);

        CREATE TABLE IF NOT EXISTS market_bars (
            terminal_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            symbol TEXT NOT NULL,
            timeframe TEXT NOT NULL,
            opened_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (terminal_id, account_key, symbol, timeframe, opened_at_utc)
        );

        CREATE TABLE IF NOT EXISTS market_ticks (
            terminal_id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            symbol TEXT NOT NULL,
            time_milliseconds INTEGER NOT NULL,
            fingerprint TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (terminal_id, account_key, symbol, time_milliseconds, fingerprint)
        );

        CREATE TABLE IF NOT EXISTS server_time_segments (
            account_key TEXT NOT NULL,
            terminal_id TEXT NOT NULL,
            from_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, terminal_id, from_utc)
        );

        CREATE TABLE IF NOT EXISTS review_saved_filters (
            id TEXT NOT NULL,
            account_key TEXT NOT NULL,
            name TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (account_key, id)
        );

        CREATE TABLE IF NOT EXISTS review_data_versions (
            account_key TEXT PRIMARY KEY,
            source_version INTEGER NOT NULL,
            metadata_version INTEGER NOT NULL,
            observation_version INTEGER NOT NULL,
            rule_version TEXT NOT NULL,
            time_version TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TRIGGER IF NOT EXISTS trg_review_source_deal_insert AFTER INSERT ON deals BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 1, 0, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET source_version = source_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_review_source_deal_update AFTER UPDATE ON deals BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 1, 0, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET source_version = source_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_review_source_trade_insert AFTER INSERT ON trades BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 1, 0, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET source_version = source_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_review_source_trade_update AFTER UPDATE ON trades BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 1, 0, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET source_version = source_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_review_metadata_update AFTER INSERT ON trade_review_metadata BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 0, 1, 0, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET metadata_version = metadata_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_review_behavior_insert AFTER INSERT ON behavior_evaluations BEGIN
            INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
            VALUES (NEW.account_key, 0, 0, 1, '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(account_key) DO UPDATE SET observation_version = observation_version + 1, updated_at_utc = excluded.updated_at_utc;
        END;
        """;

    private const string SymbolSpecificationSchema = """
        CREATE TABLE IF NOT EXISTS symbol_specifications (
            account_key TEXT NOT NULL,
            symbol TEXT NOT NULL COLLATE NOCASE,
            point NUMERIC NOT NULL,
            tick_size NUMERIC NOT NULL,
            digits INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, symbol)
        );
        """;

    private const string ReviewAndBehaviorSchema = """
        CREATE TABLE IF NOT EXISTS structured_trade_plans (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            reference_entry_price NUMERIC NULL,
            entry_low NUMERIC NULL,
            entry_high NUMERIC NULL,
            stop_price NUMERIC NULL,
            target_price NUMERIC NULL,
            strategy TEXT NOT NULL,
            setup TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            notes TEXT NOT NULL,
            is_active INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_structured_plans_scope
            ON structured_trade_plans(account_key, server_date, symbol, is_active);

        CREATE TABLE IF NOT EXISTS structured_trade_plan_links (
            plan_id TEXT NOT NULL,
            object_key TEXT NOT NULL,
            role TEXT NOT NULL,
            PRIMARY KEY (plan_id, object_key),
            FOREIGN KEY (plan_id) REFERENCES structured_trade_plans(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS trade_review_metadata (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            plan_id TEXT NULL,
            compliance_status TEXT NOT NULL,
            strategy TEXT NOT NULL,
            setup TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            user_edited INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, position_id),
            FOREIGN KEY (plan_id) REFERENCES structured_trade_plans(id)
        );

        CREATE INDEX IF NOT EXISTS ix_review_metadata_plan
            ON trade_review_metadata(account_key, plan_id, compliance_status);

        CREATE TABLE IF NOT EXISTS trade_excursions (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            minimum_pnl NUMERIC NOT NULL,
            maximum_pnl NUMERIC NOT NULL,
            initial_risk_amount NUMERIC NULL,
            planned_risk_multiple NUMERIC NULL,
            actual_risk_multiple NUMERIC NULL,
            first_sample_at_utc TEXT NOT NULL,
            last_sample_at_utc TEXT NOT NULL,
            covered_milliseconds INTEGER NOT NULL,
            holding_milliseconds INTEGER NOT NULL,
            started_at_open INTEGER NOT NULL,
            is_complete INTEGER NOT NULL,
            PRIMARY KEY (account_key, position_id)
        );

        CREATE TABLE IF NOT EXISTS account_cash_flows (
            account_key TEXT NOT NULL,
            ticket INTEGER NOT NULL,
            type TEXT NOT NULL,
            amount NUMERIC NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, ticket)
        );

        CREATE INDEX IF NOT EXISTS ix_cash_flows_time
            ON account_cash_flows(account_key, occurred_at_utc);

        CREATE TABLE IF NOT EXISTS equity_samples (
            account_key TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            server_date TEXT NOT NULL,
            balance NUMERIC NOT NULL,
            equity NUMERIC NOT NULL,
            floating_pnl NUMERIC NOT NULL,
            has_open_position INTEGER NOT NULL,
            PRIMARY KEY (account_key, captured_at_utc)
        );

        CREATE INDEX IF NOT EXISTS ix_equity_samples_scope
            ON equity_samples(account_key, server_date, captured_at_utc);

        CREATE TABLE IF NOT EXISTS drawdown_episodes (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            curve_kind TEXT NOT NULL,
            start_server_date TEXT NOT NULL,
            end_server_date TEXT NULL,
            peak_at_utc TEXT NOT NULL,
            trough_at_utc TEXT NOT NULL,
            recovered_at_utc TEXT NULL,
            peak_value NUMERIC NOT NULL,
            trough_value NUMERIC NOT NULL,
            drawdown_amount NUMERIC NOT NULL,
            drawdown_percentage NUMERIC NULL
        );

        CREATE INDEX IF NOT EXISTS ix_drawdown_scope
            ON drawdown_episodes(account_key, curve_kind, start_server_date);

        CREATE TABLE IF NOT EXISTS history_sync_state (
            account_key TEXT NOT NULL,
            range_year INTEGER NOT NULL,
            is_complete INTEGER NOT NULL,
            deal_count INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, range_year)
        );

        CREATE TABLE IF NOT EXISTS behavior_evaluations (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            position_id INTEGER NULL,
            rule TEXT NOT NULL,
            value NUMERIC NOT NULL,
            baseline NUMERIC NULL,
            threshold NUMERIC NOT NULL,
            level TEXT NOT NULL,
            triggered INTEGER NOT NULL,
            summary TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_behavior_scope
            ON behavior_evaluations(account_key, server_date, observed_at_utc);
        """;

    private const string InitialSchema = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS processed_events (
            source_instance_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            processed_at_utc TEXT NOT NULL,
            PRIMARY KEY (source_instance_id, sequence)
        );

        CREATE TABLE IF NOT EXISTS accounts (
            account_key TEXT PRIMARY KEY,
            server TEXT NOT NULL,
            login INTEGER NOT NULL,
            currency TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS trading_days (
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            settings_json TEXT NOT NULL,
            daily_state_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, server_date),
            FOREIGN KEY (account_key) REFERENCES accounts(account_key)
        );

        CREATE TABLE IF NOT EXISTS deals (
            account_key TEXT NOT NULL,
            ticket INTEGER NOT NULL,
            order_ticket INTEGER NOT NULL,
            position_id INTEGER NOT NULL,
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            entry_kind TEXT NOT NULL,
            volume NUMERIC NOT NULL,
            price NUMERIC NOT NULL,
            profit NUMERIC NOT NULL,
            commission NUMERIC NOT NULL,
            swap NUMERIC NOT NULL,
            fee NUMERIC NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, ticket)
        );

        CREATE INDEX IF NOT EXISTS ix_deals_position
            ON deals(account_key, position_id, occurred_at_utc);

        CREATE TABLE IF NOT EXISTS trades (
            account_key TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            opened_at_utc TEXT NOT NULL,
            closed_at_utc TEXT NULL,
            open_server_date TEXT NOT NULL,
            close_server_date TEXT NULL,
            entry_price NUMERIC NOT NULL,
            exit_price NUMERIC NULL,
            opening_volume NUMERIC NOT NULL,
            maximum_volume NUMERIC NOT NULL,
            remaining_volume NUMERIC NOT NULL,
            net_pnl NUMERIC NOT NULL,
            is_complete INTEGER NOT NULL,
            PRIMARY KEY (account_key, position_id)
        );

        CREATE INDEX IF NOT EXISTS ix_trades_day
            ON trades(account_key, close_server_date, closed_at_utc);

        CREATE TABLE IF NOT EXISTS positions (
            account_key TEXT NOT NULL,
            ticket INTEGER NOT NULL,
            position_id INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_key, ticket)
        );

        CREATE TABLE IF NOT EXISTS chart_objects (
            object_key TEXT PRIMARY KEY,
            terminal_id TEXT NOT NULL,
            chart_id INTEGER NOT NULL,
            object_name TEXT NOT NULL,
            symbol TEXT NOT NULL,
            timeframe TEXT NOT NULL,
            kind TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            deleted_at_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_chart_objects_chart
            ON chart_objects(terminal_id, chart_id, deleted_at_utc);

        CREATE TABLE IF NOT EXISTS chart_object_revisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            object_key TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            recorded_at_utc TEXT NOT NULL,
            is_deleted INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS plan_items (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            object_key TEXT NOT NULL,
            category TEXT NOT NULL,
            symbol TEXT NOT NULL,
            price_low NUMERIC NULL,
            price_high NUMERIC NULL,
            text TEXT NULL,
            is_active INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_plan_items_object_day
            ON plan_items(account_key, server_date, object_key);

        CREATE TABLE IF NOT EXISTS loss_zones (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            symbol TEXT NOT NULL,
            center_price NUMERIC NOT NULL,
            tolerance NUMERIC NOT NULL,
            attempt_count INTEGER NOT NULL,
            loss_count INTEGER NOT NULL,
            cumulative_loss NUMERIC NOT NULL,
            last_attempt_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_loss_zones_scope
            ON loss_zones(account_key, server_date, symbol);

        CREATE TABLE IF NOT EXISTS loss_zone_attempts (
            id TEXT PRIMARY KEY,
            zone_id TEXT NOT NULL,
            position_id INTEGER NOT NULL,
            side TEXT NOT NULL,
            entry_price NUMERIC NOT NULL,
            opening_volume NUMERIC NOT NULL,
            net_pnl NUMERIC NULL,
            opened_at_utc TEXT NOT NULL,
            closed_at_utc TEXT NULL,
            FOREIGN KEY (zone_id) REFERENCES loss_zones(id)
        );

        CREATE TABLE IF NOT EXISTS timeline_events (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            kind TEXT NOT NULL,
            summary TEXT NOT NULL,
            details_json TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_timeline_scope
            ON timeline_events(account_key, server_date, occurred_at_utc);

        CREATE TABLE IF NOT EXISTS settings (
            scope_key TEXT NOT NULL,
            setting_key TEXT NOT NULL,
            value_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (scope_key, setting_key)
        );

        CREATE TABLE IF NOT EXISTS alert_deliveries (
            id TEXT PRIMARY KEY,
            account_key TEXT NOT NULL,
            server_date TEXT NOT NULL,
            alert_key TEXT NOT NULL,
            delivered_at_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_alert_delivery_key
            ON alert_deliveries(account_key, server_date, alert_key);
        """;
}
