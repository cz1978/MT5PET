using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Persistence;

public sealed partial class AppDatabase
{
    private const int MaximumCachedBarsPerAccount = 250_000;
    private const int MaximumCachedTicksPerAccount = 500_000;
    private const int MaximumMarketRangesPerAccount = 2_000;

    public async Task<ReviewDataVersion> LoadReviewDataVersionAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadReviewVersionAsync(connection, accountKey, cancellationToken);
    }

    public async Task<ReviewWorkspaceData> LoadWorkspaceAsync(
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "结束日期不能早于开始日期。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteReadSnapshotCommandAsync(connection, "BEGIN DEFERRED;", cancellationToken);
        try
        {
            var trades = await LoadSnapshotTradesAsync(connection, accountKey, from, to, cancellationToken);
            var positionIds = trades.Select(item => item.PositionId).ToArray();
            if (ReviewReadCheckpointAsync is { } checkpoint)
            {
                await checkpoint("workspace-trades-loaded", cancellationToken);
            }
            var deals = await LoadSnapshotDealsAsync(connection, accountKey, from, to, cancellationToken);
            var metadata = await LoadSnapshotMetadataAsync(connection, accountKey, positionIds, cancellationToken);
            var excursions = await LoadSnapshotExcursionsAsync(connection, accountKey, positionIds, cancellationToken);
            var historyStates = await LoadSnapshotHistoryStatesAsync(connection, accountKey, cancellationToken);
            var dailyStates = await LoadSnapshotDailyStatesAsync(connection, accountKey, from, to, cancellationToken);
            var equitySamples = await LoadSnapshotEquitySamplesAsync(connection, accountKey, from, to, cancellationToken);
            var cashFlowFromUtc = from == DateOnly.MinValue
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
            var cashFlowToUtc = to.DayNumber >= DateOnly.MaxValue.DayNumber - 1
                ? DateTimeOffset.MaxValue
                : new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
            var cashFlows = await LoadSnapshotCashFlowsAsync(
                connection, accountKey, cashFlowFromUtc, cashFlowToUtc, cancellationToken);
            IReadOnlyList<DateOnly> dataGapDates = [];
            if (to.DayNumber - from.DayNumber <= 3660)
            {
                var completeYears = historyStates.Where(item => item.IsComplete).Select(item => item.RangeYear).ToHashSet();
                var gaps = new List<DateOnly>();
                for (var date = from; date <= to; date = date.AddDays(1))
                {
                    if (!completeYears.Contains(date.Year))
                    {
                        gaps.Add(date);
                    }
                }
                dataGapDates = gaps;
            }

            var currency = await LoadCurrencyAsync(connection, accountKey, cancellationToken);
            var documents = await LoadDocumentsAsync(connection, accountKey, positionIds, cancellationToken);
            var journals = await LoadDailyJournalsAsync(connection, accountKey, from, to, cancellationToken);
            var playbooks = await LoadJsonListAsync<PlaybookVersion>(connection,
                "SELECT payload_json FROM playbook_versions WHERE account_key = $account ORDER BY effective_from_utc;",
                accountKey, cancellationToken);
            var assessments = await LoadAssessmentsAsync(connection, accountKey, positionIds, cancellationToken);
            var campaigns = await LoadJsonListAsync<TradeCampaign>(connection,
                "SELECT payload_json FROM trade_campaigns WHERE account_key = $account ORDER BY updated_at_utc DESC;",
                accountKey, cancellationToken);
            var behaviors = await LoadBehaviorsAsync(connection, accountKey, from, to, cancellationToken);
            var goals = await LoadJsonListAsync<ImprovementGoal>(connection,
                "SELECT payload_json FROM improvement_goals WHERE account_key = $account ORDER BY updated_at_utc DESC;",
                accountKey, cancellationToken);
            var observations = await LoadDatedJsonAsync<GoalObservation>(connection, "goal_observations", accountKey, from, to, cancellationToken);
            var opportunities = await LoadDatedJsonAsync<OpportunityRecord>(connection, "opportunity_records", accountKey, from, to, cancellationToken);
            var opportunityAttachments = await LoadAttachmentsForKindAsync(
                connection, accountKey, "opportunity", opportunities.Select(item => item.Id).ToArray(), cancellationToken);
            var ranges = await LoadMarketRangesAsync(connection, accountKey, from, to, cancellationToken);
            var version = await LoadReviewVersionAsync(connection, accountKey, cancellationToken);
            var periodReviews = await LoadPeriodReviewsAsync(connection, accountKey, from, to, cancellationToken);
            var savedFilters = await LoadJsonListAsync<ReviewSavedFilter>(connection,
                "SELECT payload_json FROM review_saved_filters WHERE account_key=$account ORDER BY name;",
                accountKey, cancellationToken);
            var timeSegments = await LoadJsonListAsync<ServerTimeSegment>(connection,
                "SELECT payload_json FROM server_time_segments WHERE account_key=$account ORDER BY from_utc;",
                accountKey, cancellationToken);
            var tradingSessions = await LoadJsonListAsync<TradingSessionDefinition>(connection,
                "SELECT payload_json FROM trading_session_definitions WHERE account_key=$account ORDER BY name;",
                accountKey, cancellationToken);

            var result = new ReviewWorkspaceData(
                accountKey, currency, trades, deals, metadata, documents, excursions, journals,
                playbooks, assessments, campaigns, behaviors, goals, observations, opportunities,
                ranges, dataGapDates, version, periodReviews, savedFilters, timeSegments, opportunityAttachments,
                dailyStates, equitySamples, cashFlows, tradingSessions);
            await ExecuteReadSnapshotCommandAsync(connection, "COMMIT;", cancellationToken);
            return result;
        }
        catch
        {
            await TryRollbackReadSnapshotAsync(connection);
            throw;
        }
    }

    public async Task<TradeDetailData?> LoadTradeDetailAsync(
        TradeKey key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteReadSnapshotCommandAsync(connection, "BEGIN DEFERRED;", cancellationToken);
        try
        {
            var detail = await LoadTradeDetailFromSnapshotAsync(connection, key, cancellationToken);
            await ExecuteReadSnapshotCommandAsync(connection, "COMMIT;", cancellationToken);
            return detail;
        }
        catch
        {
            await TryRollbackReadSnapshotAsync(connection);
            throw;
        }
    }

    public async Task<IReadOnlyList<TradeDetailData>> LoadTradeDetailsAsync(
        IReadOnlyCollection<TradeKey> keys,
        CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
        {
            return [];
        }
        await using var connection = await CreateIsolatedReadSnapshotAsync(cancellationToken);
        var details = new List<TradeDetailData>(keys.Count);
        foreach (var key in keys.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await LoadTradeDetailFromSnapshotAsync(connection, key, cancellationToken);
            if (detail is not null)
            {
                details.Add(detail);
            }
        }
        return details;
    }

    public async Task<TradeReviewDocument?> LoadTradeReviewDocumentAsync(
        TradeKey key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadTradeReviewDocumentAsync(connection, key, cancellationToken);
    }

    public async Task<ReviewSaveResult<TradeReviewDocument>> SaveTradeReviewDocumentAsync(
        TradeReviewDocument document,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var trade = connection.CreateCommand())
        {
            trade.Transaction = (SqliteTransaction)transaction;
            trade.CommandText = "SELECT is_complete FROM trades WHERE account_key=$account AND position_id=$position;";
            trade.Parameters.AddWithValue("$account", document.TradeKey.AccountKey);
            trade.Parameters.AddWithValue("$position", document.TradeKey.PositionId);
            var complete = await trade.ExecuteScalarAsync(cancellationToken);
            if (complete is null)
            {
                return ReviewSaveResult<TradeReviewDocument>.Missing("目标交易不存在，不能创建孤立复盘文档。");
            }
            if (Convert.ToInt32(complete, CultureInfo.InvariantCulture) != 1)
            {
                return ReviewSaveResult<TradeReviewDocument>.Validation("持仓中的交易不能创建完整交易复盘。");
            }
        }
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM trade_review_documents WHERE account_key = $account AND position_id = $id;",
            document.TradeKey.AccountKey, document.TradeKey.PositionId, cancellationToken);
        if (current != expectedRevision)
        {
            return ReviewSaveResult<TradeReviewDocument>.Conflict($"复盘已被修改（当前修订 {current}，提交基于 {expectedRevision}）。");
        }

        var saved = document.Normalize() with { Revision = current + 1 };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO trade_review_documents(account_key, position_id, status, revision, source_version, rule_version, updated_at_utc, payload_json)
                VALUES ($account, $position, $status, $revision, $source, $rule, $updated, $payload)
                ON CONFLICT(account_key, position_id) DO UPDATE SET status=excluded.status, revision=excluded.revision,
                    source_version=excluded.source_version, rule_version=excluded.rule_version,
                    updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$account", saved.TradeKey.AccountKey);
            command.Parameters.AddWithValue("$position", saved.TradeKey.PositionId);
            command.Parameters.AddWithValue("$status", saved.Status.ToString());
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$source", saved.SourceVersion);
            command.Parameters.AddWithValue("$rule", saved.RuleVersion);
            command.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
            command.Parameters.AddWithValue("$payload", Serialize(saved));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "trade", saved.TradeKey.AccountKey,
            saved.TradeKey.PositionId.ToString(CultureInfo.InvariantCulture), saved.Revision, saved, saved.UpdatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, saved.TradeKey.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<TradeReviewDocument>.Saved(saved);
    }

    public async Task<ReviewSaveResult<ReviewBulkEditResult>> SaveBulkReviewAsync(
        IReadOnlyList<ReviewBulkWriteItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0 || items.Select(item => item.TradeKey.AccountKey).Distinct(StringComparer.Ordinal).Count() != 1 ||
            items.Select(item => item.TradeKey.PositionId).Distinct().Count() != items.Count ||
            items.Any(item => item.Metadata is null && item.Document is null ||
                              item.Metadata is not null && (item.Metadata.AccountKey != item.TradeKey.AccountKey ||
                                                            item.Metadata.PositionId != item.TradeKey.PositionId) ||
                              item.Document is not null && item.Document.TradeKey != item.TradeKey))
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Validation("批量复盘写入必须是同一账户的不重复交易集合。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in items)
        {
            await using (var trade = connection.CreateCommand())
            {
                trade.Transaction = (SqliteTransaction)transaction;
                trade.CommandText = "SELECT is_complete FROM trades WHERE account_key=$account AND position_id=$position;";
                trade.Parameters.AddWithValue("$account", item.TradeKey.AccountKey);
                trade.Parameters.AddWithValue("$position", item.TradeKey.PositionId);
                var complete = await trade.ExecuteScalarAsync(cancellationToken);
                if (complete is null || Convert.ToInt32(complete, CultureInfo.InvariantCulture) != 1)
                {
                    return ReviewSaveResult<ReviewBulkEditResult>.Validation(
                        $"交易 #{item.TradeKey.PositionId} 不存在、跨账户或尚未完成。");
                }
            }

            if (item.Metadata is not null)
            {
                await using var metadata = connection.CreateCommand();
                metadata.Transaction = (SqliteTransaction)transaction;
                metadata.CommandText = "SELECT updated_at_utc FROM trade_review_metadata WHERE account_key=$account AND position_id=$position;";
                metadata.Parameters.AddWithValue("$account", item.TradeKey.AccountKey);
                metadata.Parameters.AddWithValue("$position", item.TradeKey.PositionId);
                var current = await metadata.ExecuteScalarAsync(cancellationToken) as string;
                var expected = item.ExpectedMetadataUpdatedAtUtc is null ? null : Format(item.ExpectedMetadataUpdatedAtUtc.Value);
                if (!string.Equals(current, expected, StringComparison.Ordinal))
                {
                    return ReviewSaveResult<ReviewBulkEditResult>.Conflict(
                        $"交易 #{item.TradeKey.PositionId} 的归类已变化，批量操作未写入。");
                }
            }

            if (item.Document is not null)
            {
                var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
                    "SELECT revision FROM trade_review_documents WHERE account_key=$account AND position_id=$id;",
                    item.TradeKey.AccountKey, item.TradeKey.PositionId, cancellationToken);
                if (current != item.ExpectedDocumentRevision)
                {
                    return ReviewSaveResult<ReviewBulkEditResult>.Conflict(
                        $"交易 #{item.TradeKey.PositionId} 的复盘已变化，批量操作未写入。");
                }
            }
        }

        foreach (var item in items)
        {
            if (item.Metadata is not null)
            {
                await using var metadata = connection.CreateCommand();
                metadata.Transaction = (SqliteTransaction)transaction;
                metadata.CommandText = """
                    INSERT INTO trade_review_metadata(
                        account_key, position_id, plan_id, compliance_status, strategy, setup,
                        tags_json, user_edited, updated_at_utc)
                    VALUES ($account, $position, $plan, $status, $strategy, $setup, $tags, $edited, $updated)
                    ON CONFLICT(account_key, position_id) DO UPDATE SET
                        plan_id=excluded.plan_id, compliance_status=excluded.compliance_status,
                        strategy=excluded.strategy, setup=excluded.setup, tags_json=excluded.tags_json,
                        user_edited=excluded.user_edited, updated_at_utc=excluded.updated_at_utc;
                    """;
                metadata.Parameters.AddWithValue("$account", item.Metadata.AccountKey);
                metadata.Parameters.AddWithValue("$position", item.Metadata.PositionId);
                metadata.Parameters.AddWithValue("$plan", DbValue(item.Metadata.PlanId));
                metadata.Parameters.AddWithValue("$status", item.Metadata.ComplianceStatus.ToString());
                metadata.Parameters.AddWithValue("$strategy", item.Metadata.Strategy.Trim());
                metadata.Parameters.AddWithValue("$setup", item.Metadata.Setup.Trim());
                metadata.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(NormalizeTags(item.Metadata.Tags), ProtocolJson.Options));
                metadata.Parameters.AddWithValue("$edited", item.Metadata.UserEdited ? 1 : 0);
                metadata.Parameters.AddWithValue("$updated", Format(item.Metadata.UpdatedAtUtc));
                await metadata.ExecuteNonQueryAsync(cancellationToken);
            }

            if (item.Document is not null)
            {
                var saved = item.Document.Normalize() with { Revision = item.ExpectedDocumentRevision + 1 };
                await using var document = connection.CreateCommand();
                document.Transaction = (SqliteTransaction)transaction;
                document.CommandText = """
                    INSERT INTO trade_review_documents(account_key, position_id, status, revision, source_version, rule_version, updated_at_utc, payload_json)
                    VALUES ($account, $position, $status, $revision, $source, $rule, $updated, $payload)
                    ON CONFLICT(account_key, position_id) DO UPDATE SET status=excluded.status, revision=excluded.revision,
                        source_version=excluded.source_version, rule_version=excluded.rule_version,
                        updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
                    """;
                document.Parameters.AddWithValue("$account", saved.TradeKey.AccountKey);
                document.Parameters.AddWithValue("$position", saved.TradeKey.PositionId);
                document.Parameters.AddWithValue("$status", saved.Status.ToString());
                document.Parameters.AddWithValue("$revision", saved.Revision);
                document.Parameters.AddWithValue("$source", saved.SourceVersion);
                document.Parameters.AddWithValue("$rule", saved.RuleVersion);
                document.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
                document.Parameters.AddWithValue("$payload", Serialize(saved));
                await document.ExecuteNonQueryAsync(cancellationToken);
                await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "trade", saved.TradeKey.AccountKey,
                    saved.TradeKey.PositionId.ToString(CultureInfo.InvariantCulture), saved.Revision, saved,
                    saved.UpdatedAtUtc, cancellationToken);
                await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction,
                    saved.TradeKey.AccountKey, "metadata", cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<ReviewBulkEditResult>.Saved(new ReviewBulkEditResult(items.Count));
    }

    public async Task<ReviewSaveResult<DailyJournal>> SaveDailyJournalAsync(
        DailyJournal journal,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Format(journal.ServerDate);
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM daily_journals WHERE account_key = $account AND server_date = $id;",
            journal.AccountKey, id, cancellationToken);
        if (current != expectedRevision)
        {
            return ReviewSaveResult<DailyJournal>.Conflict($"日记已被修改（当前修订 {current}，提交基于 {expectedRevision}）。");
        }

        var saved = journal with { Revision = current + 1 };
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO daily_journals(account_key, server_date, status, revision, updated_at_utc, payload_json)
            VALUES ($account, $date, $status, $revision, $updated, $payload)
            ON CONFLICT(account_key, server_date) DO UPDATE SET status=excluded.status, revision=excluded.revision,
                updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$account", saved.AccountKey);
        command.Parameters.AddWithValue("$date", id);
        command.Parameters.AddWithValue("$status", saved.Status.ToString());
        command.Parameters.AddWithValue("$revision", saved.Revision);
        command.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
        command.Parameters.AddWithValue("$payload", Serialize(saved));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "daily", saved.AccountKey, id,
            saved.Revision, saved, saved.UpdatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, saved.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<DailyJournal>.Saved(saved);
    }

    public async Task<ReviewSaveResult<PeriodReview>> SavePeriodReviewAsync(
        PeriodReview review,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM period_reviews WHERE account_key = $account AND id = $id;",
            review.AccountKey, review.Id, cancellationToken);
        if (current != expectedRevision)
        {
            return ReviewSaveResult<PeriodReview>.Conflict("周期复盘已被其他编辑覆盖，请刷新后重试。");
        }

        var saved = review with { Revision = current + 1 };
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO period_reviews(id, account_key, from_server_date, to_server_date, revision, updated_at_utc, payload_json)
            VALUES ($id, $account, $from, $to, $revision, $updated, $payload)
            ON CONFLICT(account_key, id) DO UPDATE SET from_server_date=excluded.from_server_date,
                to_server_date=excluded.to_server_date, revision=excluded.revision,
                updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", saved.Id);
        command.Parameters.AddWithValue("$account", saved.AccountKey);
        command.Parameters.AddWithValue("$from", Format(saved.FromServerDate));
        command.Parameters.AddWithValue("$to", Format(saved.ToServerDate));
        command.Parameters.AddWithValue("$revision", saved.Revision);
        command.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
        command.Parameters.AddWithValue("$payload", Serialize(saved));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "period", saved.AccountKey, saved.Id,
            saved.Revision, saved, saved.UpdatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, saved.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<PeriodReview>.Saved(saved);
    }

    public async Task<ReviewSaveResult<PlaybookVersion>> SavePlaybookVersionAsync(
        PlaybookVersion playbook,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentVersions = new List<PlaybookVersion>();
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = (SqliteTransaction)transaction;
            current.CommandText = """
                SELECT payload_json FROM playbook_versions
                WHERE account_key=$account AND playbook_id=$playbook
                ORDER BY version;
                """;
            current.Parameters.AddWithValue("$account", playbook.AccountKey);
            current.Parameters.AddWithValue("$playbook", playbook.PlaybookId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                currentVersions.Add(Deserialize<PlaybookVersion>(reader.GetString(0)));
            }
        }
        var expectedVersion = currentVersions.Count == 0 ? 1 : currentVersions.Max(item => item.Version) + 1;
        if (playbook.Version != expectedVersion)
        {
            return ReviewSaveResult<PlaybookVersion>.Conflict($"策略新版本应为 v{expectedVersion}，请刷新后重试。");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO playbook_versions(id, playbook_id, account_key, version, effective_from_utc, is_active, payload_json)
            VALUES ($id, $playbook, $account, $version, $effective, $active, $payload);
            """;
        command.Parameters.AddWithValue("$id", playbook.Id);
        command.Parameters.AddWithValue("$playbook", playbook.PlaybookId);
        command.Parameters.AddWithValue("$account", playbook.AccountKey);
        command.Parameters.AddWithValue("$version", playbook.Version);
        command.Parameters.AddWithValue("$effective", Format(playbook.EffectiveFromUtc));
        command.Parameters.AddWithValue("$active", playbook.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$payload", Serialize(playbook));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return ReviewSaveResult<PlaybookVersion>.Conflict("同一策略版本已经存在；请创建更高版本。");
        }

        if (playbook.IsActive)
        {
            foreach (var previous in currentVersions.Where(item => item.IsActive))
            {
                await using var deactivate = connection.CreateCommand();
                deactivate.Transaction = (SqliteTransaction)transaction;
                deactivate.CommandText = """
                    UPDATE playbook_versions SET is_active=0, payload_json=$payload
                    WHERE id=$id AND account_key=$account;
                    """;
                deactivate.Parameters.AddWithValue("$payload", Serialize(previous with { IsActive = false }));
                deactivate.Parameters.AddWithValue("$id", previous.Id);
                deactivate.Parameters.AddWithValue("$account", previous.AccountKey);
                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, playbook.AccountKey, "rule", cancellationToken, $"{playbook.PlaybookId}:{playbook.Version}");
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<PlaybookVersion>.Saved(playbook);
    }

    public async Task<ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>> SaveRuleAssessmentsAsync(
        TradeKey key,
        IReadOnlyList<TradeRuleAssessment> assessments,
        CancellationToken cancellationToken = default)
    {
        if (assessments.Any(item => item.TradeKey != key))
        {
            return ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>.Validation("执行评价包含其他账户或交易。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM trade_rule_assessments WHERE account_key=$account AND position_id=$position;";
            delete.Parameters.AddWithValue("$account", key.AccountKey);
            delete.Parameters.AddWithValue("$position", key.PositionId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var assessment in assessments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO trade_rule_assessments(account_key, position_id, playbook_version_id, rule_id, status, updated_at_utc, payload_json)
                VALUES ($account, $position, $playbook, $rule, $status, $updated, $payload);
                """;
            insert.Parameters.AddWithValue("$account", key.AccountKey);
            insert.Parameters.AddWithValue("$position", key.PositionId);
            insert.Parameters.AddWithValue("$playbook", assessment.PlaybookVersionId);
            insert.Parameters.AddWithValue("$rule", assessment.RuleId);
            insert.Parameters.AddWithValue("$status", assessment.Status.ToString());
            insert.Parameters.AddWithValue("$updated", Format(assessment.UpdatedAtUtc));
            insert.Parameters.AddWithValue("$payload", Serialize(assessment));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, key.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>.Saved(assessments);
    }

    public async Task<ReviewSaveResult<TradeCampaign>> SaveCampaignAsync(
        TradeCampaign campaign,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM trade_campaigns WHERE account_key=$account AND id=$id;",
            campaign.AccountKey, campaign.Id, cancellationToken);
        if (current != expectedRevision)
        {
            return ReviewSaveResult<TradeCampaign>.Conflict("交易分组已被修改，请刷新后重试。");
        }

        var duplicate = campaign.PositionIds.GroupBy(item => item).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return ReviewSaveResult<TradeCampaign>.Validation($"交易 {duplicate.Key} 在分组中重复。");
        }

        var saved = campaign with { Revision = current + 1, PositionIds = campaign.PositionIds.Distinct().ToArray() };
        try
        {
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = (SqliteTransaction)transaction;
                upsert.CommandText = """
                    INSERT INTO trade_campaigns(id, account_key, symbol, revision, updated_at_utc, payload_json)
                    VALUES ($id, $account, $symbol, $revision, $updated, $payload)
                    ON CONFLICT(account_key, id) DO UPDATE SET symbol=excluded.symbol, revision=excluded.revision,
                        updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
                    """;
                upsert.Parameters.AddWithValue("$id", saved.Id);
                upsert.Parameters.AddWithValue("$account", saved.AccountKey);
                upsert.Parameters.AddWithValue("$symbol", saved.Symbol);
                upsert.Parameters.AddWithValue("$revision", saved.Revision);
                upsert.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
                upsert.Parameters.AddWithValue("$payload", Serialize(saved));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM trade_campaign_members WHERE account_key=$account AND campaign_id=$id;";
                delete.Parameters.AddWithValue("$account", saved.AccountKey);
                delete.Parameters.AddWithValue("$id", saved.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var positionId in saved.PositionIds)
            {
                await using var member = connection.CreateCommand();
                member.Transaction = (SqliteTransaction)transaction;
                member.CommandText = "INSERT INTO trade_campaign_members(account_key, campaign_id, position_id) VALUES ($account, $campaign, $position);";
                member.Parameters.AddWithValue("$account", saved.AccountKey);
                member.Parameters.AddWithValue("$campaign", saved.Id);
                member.Parameters.AddWithValue("$position", positionId);
                await member.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return ReviewSaveResult<TradeCampaign>.Validation("一笔交易只能属于一个交易分组。");
        }

        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "campaign", saved.AccountKey, saved.Id,
            saved.Revision, saved, saved.UpdatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, saved.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<TradeCampaign>.Saved(saved);
    }

    public Task<ReviewSaveResult<ImprovementGoal>> SaveGoalAsync(
        ImprovementGoal goal,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        SaveSimpleRevisionedAsync("improvement_goals", "goal", goal.AccountKey, goal.Id,
            expectedRevision, goal.UpdatedAtUtc, goal,
            (saved, revision) => saved with { Revision = revision },
            "status", goal.Status.ToString(), cancellationToken);

    public async Task<ReviewSaveResult<ImprovementGoal>> SaveGoalVersionAsync(
        ImprovementGoal previousVersion,
        int expectedPreviousRevision,
        ImprovementGoal nextVersion,
        CancellationToken cancellationToken = default)
    {
        if (previousVersion.AccountKey != nextVersion.AccountKey || previousVersion.Id == nextVersion.Id)
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("目标版本必须属于同一账户并使用不同身份。");
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM improvement_goals WHERE account_key=$account AND id=$id;",
            previousVersion.AccountKey, previousVersion.Id, cancellationToken);
        if (current != expectedPreviousRevision)
        {
            return ReviewSaveResult<ImprovementGoal>.Conflict("上一目标版本已变化，请刷新后重试。");
        }
        var nextCurrent = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            "SELECT revision FROM improvement_goals WHERE account_key=$account AND id=$id;",
            nextVersion.AccountKey, nextVersion.Id, cancellationToken);
        if (nextCurrent != 0)
        {
            return ReviewSaveResult<ImprovementGoal>.Conflict("目标新版本身份已存在，请刷新后重试。");
        }

        var archived = previousVersion with { Revision = current + 1 };
        var saved = nextVersion with { Revision = 1 };
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE improvement_goals SET status=$status, revision=$revision, updated_at_utc=$updated, payload_json=$payload WHERE account_key=$account AND id=$id AND revision=$expected;";
            update.Parameters.AddWithValue("$status", archived.Status.ToString());
            update.Parameters.AddWithValue("$revision", archived.Revision);
            update.Parameters.AddWithValue("$updated", Format(archived.UpdatedAtUtc));
            update.Parameters.AddWithValue("$payload", Serialize(archived));
            update.Parameters.AddWithValue("$account", archived.AccountKey);
            update.Parameters.AddWithValue("$id", archived.Id);
            update.Parameters.AddWithValue("$expected", expectedPreviousRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return ReviewSaveResult<ImprovementGoal>.Conflict("上一目标版本在保存期间发生变化。");
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO improvement_goals(id, account_key, status, revision, updated_at_utc, payload_json) VALUES ($id, $account, $status, $revision, $updated, $payload);";
            insert.Parameters.AddWithValue("$id", saved.Id);
            insert.Parameters.AddWithValue("$account", saved.AccountKey);
            insert.Parameters.AddWithValue("$status", saved.Status.ToString());
            insert.Parameters.AddWithValue("$revision", saved.Revision);
            insert.Parameters.AddWithValue("$updated", Format(saved.UpdatedAtUtc));
            insert.Parameters.AddWithValue("$payload", Serialize(saved));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "goal", archived.AccountKey,
            archived.Id, archived.Revision, archived, archived.UpdatedAtUtc, cancellationToken);
        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, "goal", saved.AccountKey,
            saved.Id, saved.Revision, saved, saved.UpdatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(
            connection, (SqliteTransaction)transaction, saved.AccountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<ImprovementGoal>.Saved(saved);
    }

    public Task<ReviewSaveResult<OpportunityRecord>> SaveOpportunityAsync(
        OpportunityRecord opportunity,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        SaveSimpleRevisionedAsync("opportunity_records", "opportunity", opportunity.AccountKey, opportunity.Id,
            expectedRevision, opportunity.RecordedAtUtc, opportunity,
            (saved, revision) => saved with { Revision = revision },
            "kind", opportunity.Kind.ToString(), cancellationToken,
            ("server_date", Format(opportunity.ServerDate)), ("symbol", opportunity.Symbol));

    public Task<ReviewSaveResult<ReviewSavedFilter>> SaveFilterAsync(
        ReviewSavedFilter filter,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        SaveSimpleRevisionedAsync("review_saved_filters", "filter", filter.AccountKey, filter.Id,
            expectedRevision, filter.UpdatedAtUtc, filter,
            (saved, revision) => saved with { Revision = revision },
            "name", filter.Name, cancellationToken);

    public Task<ReviewSaveResult<TradingSessionDefinition>> SaveTradingSessionAsync(
        TradingSessionDefinition session,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        SaveSimpleRevisionedAsync("trading_session_definitions", "trading-session", session.AccountKey, session.Id,
            expectedRevision, session.UpdatedAtUtc, session,
            (saved, revision) => saved with { Revision = revision },
            "name", session.Name, cancellationToken);

    public async Task<IReadOnlyList<ReviewSavedFilter>> LoadFiltersAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadJsonListAsync<ReviewSavedFilter>(connection,
            "SELECT payload_json FROM review_saved_filters WHERE account_key=$account ORDER BY name;",
            accountKey, cancellationToken);
    }

    public async Task SaveServerTimeSegmentAsync(
        ServerTimeSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.TerminalId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        ServerTimeSegment? currentSegment = null;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = (SqliteTransaction)transaction;
            current.CommandText = """
                SELECT payload_json FROM server_time_segments
                WHERE account_key=$account AND terminal_id=$terminal
                ORDER BY from_utc DESC LIMIT 1;
                """;
            current.Parameters.AddWithValue("$account", segment.AccountKey);
            current.Parameters.AddWithValue("$terminal", segment.TerminalId);
            currentSegment = DeserializeOrNull<ServerTimeSegment>(
                await current.ExecuteScalarAsync(cancellationToken) as string);
        }
        if (currentSegment is not null && currentSegment.ToUtc is null &&
            currentSegment.UtcOffsetSeconds == segment.UtcOffsetSeconds &&
            currentSegment.TimeBasis == segment.TimeBasis &&
            string.Equals(currentSegment.SourceVersion, segment.SourceVersion, StringComparison.Ordinal))
        {
            return;
        }
        var normalized = segment with { ToUtc = null };
        if (currentSegment is not null && currentSegment.ToUtc is null)
        {
            var closed = currentSegment with { ToUtc = normalized.FromUtc };
            await using var close = connection.CreateCommand();
            close.Transaction = (SqliteTransaction)transaction;
            close.CommandText = """
                UPDATE server_time_segments SET payload_json=$payload
                WHERE account_key=$account AND terminal_id=$terminal AND from_utc=$from;
                """;
            close.Parameters.AddWithValue("$payload", Serialize(closed));
            close.Parameters.AddWithValue("$account", closed.AccountKey);
            close.Parameters.AddWithValue("$terminal", closed.TerminalId);
            close.Parameters.AddWithValue("$from", Format(closed.FromUtc));
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO server_time_segments(account_key, terminal_id, from_utc, payload_json)
                VALUES ($account, $terminal, $from, $payload);
                """;
            insert.Parameters.AddWithValue("$account", normalized.AccountKey);
            insert.Parameters.AddWithValue("$terminal", normalized.TerminalId);
            insert.Parameters.AddWithValue("$from", Format(normalized.FromUtc));
            insert.Parameters.AddWithValue("$payload", Serialize(normalized));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var version = $"{normalized.TerminalId}|{normalized.FromUtc:O}|{normalized.UtcOffsetSeconds}|{normalized.TimeBasis}|{normalized.SourceVersion}";
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, normalized.AccountKey,
            "time", cancellationToken, version);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveMarketDataAsync(
        MarketDataRange range,
        IReadOnlyList<MarketBar> bars,
        IReadOnlyList<MarketTick> ticks,
        CancellationToken cancellationToken = default)
    {
        if (bars.Any(item => item.AccountKey != range.AccountKey || item.TerminalId != range.TerminalId ||
                             !item.Symbol.Equals(range.Symbol, StringComparison.OrdinalIgnoreCase) || item.Timeframe != range.Timeframe) ||
            ticks.Any(item => item.AccountKey != range.AccountKey || item.TerminalId != range.TerminalId ||
                              !item.Symbol.Equals(range.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("行情数据与请求的账户、终端或品种不一致。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO market_data_ranges(request_id, terminal_id, account_key, symbol, timeframe,
                    requested_from_utc, requested_to_utc, precision, coverage, updated_at_utc, payload_json)
                VALUES ($request, $terminal, $account, $symbol, $timeframe, $from, $to, $precision, $coverage, $updated, $payload)
                ON CONFLICT(account_key, request_id) DO UPDATE SET coverage=excluded.coverage,
                    updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$request", range.RequestId);
            command.Parameters.AddWithValue("$terminal", range.TerminalId);
            command.Parameters.AddWithValue("$account", range.AccountKey);
            command.Parameters.AddWithValue("$symbol", range.Symbol);
            command.Parameters.AddWithValue("$timeframe", range.Timeframe);
            command.Parameters.AddWithValue("$from", Format(range.RequestedFromUtc));
            command.Parameters.AddWithValue("$to", Format(range.RequestedToUtc));
            command.Parameters.AddWithValue("$precision", range.Precision.ToString());
            command.Parameters.AddWithValue("$coverage", range.Coverage.ToString());
            command.Parameters.AddWithValue("$updated", Format(range.UpdatedAtUtc));
            command.Parameters.AddWithValue("$payload", Serialize(range));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var bar in bars)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO market_bars(terminal_id, account_key, symbol, timeframe, opened_at_utc, payload_json)
                VALUES ($terminal, $account, $symbol, $timeframe, $opened, $payload);
                """;
            command.Parameters.AddWithValue("$terminal", bar.TerminalId);
            command.Parameters.AddWithValue("$account", bar.AccountKey);
            command.Parameters.AddWithValue("$symbol", bar.Symbol);
            command.Parameters.AddWithValue("$timeframe", bar.Timeframe);
            command.Parameters.AddWithValue("$opened", Format(bar.OpenedAtUtc));
            command.Parameters.AddWithValue("$payload", Serialize(bar));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tick in ticks)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO market_ticks(terminal_id, account_key, symbol, time_milliseconds, fingerprint, occurred_at_utc, payload_json)
                VALUES ($terminal, $account, $symbol, $milliseconds, $fingerprint, $occurred, $payload);
                """;
            command.Parameters.AddWithValue("$terminal", tick.TerminalId);
            command.Parameters.AddWithValue("$account", tick.AccountKey);
            command.Parameters.AddWithValue("$symbol", tick.Symbol);
            command.Parameters.AddWithValue("$milliseconds", tick.TimeMilliseconds);
            command.Parameters.AddWithValue("$fingerprint", tick.Fingerprint);
            command.Parameters.AddWithValue("$occurred", Format(tick.OccurredAtUtc));
            command.Parameters.AddWithValue("$payload", Serialize(tick));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await TrimMarketDataCacheAsync(connection, (SqliteTransaction)transaction, range.AccountKey, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, range.AccountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReviewCacheCleanupResult> ClearMarketDataCacheAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bars = await DeleteMarketRowsAsync(connection, (SqliteTransaction)transaction,
            "market_bars", accountKey, cancellationToken);
        var ticks = await DeleteMarketRowsAsync(connection, (SqliteTransaction)transaction,
            "market_ticks", accountKey, cancellationToken);
        var ranges = await DeleteMarketRowsAsync(connection, (SqliteTransaction)transaction,
            "market_data_ranges", accountKey, cancellationToken);
        if (ranges + bars + ticks > 0)
        {
            await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, accountKey,
                "observation", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ReviewCacheCleanupResult(ranges, bars, ticks);
    }

    public async Task<(MarketDataRange? Range, IReadOnlyList<MarketBar> Bars, IReadOnlyList<MarketTick> Ticks)> LoadMarketDataAsync(
        string accountKey,
        string terminalId,
        string symbol,
        string timeframe,
        MarketDataPrecision precision,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        MarketDataRange? range;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT payload_json FROM market_data_ranges
                WHERE account_key=$account AND terminal_id=$terminal AND symbol=$symbol COLLATE NOCASE
                    AND timeframe=$timeframe AND requested_from_utc <= $from AND requested_to_utc >= $to
                    AND precision=$precision
                ORDER BY updated_at_utc DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$terminal", terminalId);
            command.Parameters.AddWithValue("$symbol", symbol);
            command.Parameters.AddWithValue("$timeframe", timeframe);
            command.Parameters.AddWithValue("$precision", precision.ToString());
            command.Parameters.AddWithValue("$from", Format(fromUtc));
            command.Parameters.AddWithValue("$to", Format(toUtc));
            range = DeserializeOrNull<MarketDataRange>(await command.ExecuteScalarAsync(cancellationToken) as string);
        }

        var bars = precision == MarketDataPrecision.Ticks
            ? []
            : await LoadMarketItemsAsync<MarketBar>(connection, "market_bars", accountKey, terminalId, symbol,
                timeframe, fromUtc, toUtc, cancellationToken);
        var ticks = precision == MarketDataPrecision.Ticks
            ? await LoadMarketItemsAsync<MarketTick>(connection, "market_ticks", accountKey, terminalId, symbol,
                null, fromUtc, toUtc, cancellationToken)
            : [];
        return (range, bars, ticks);
    }

    private static async Task TrimMarketDataCacheAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountKey,
        CancellationToken cancellationToken)
    {
        var removedItems = 0;
        removedItems += await TrimTableAsync("market_bars", "opened_at_utc", MaximumCachedBarsPerAccount);
        removedItems += await TrimTableAsync("market_ticks", "occurred_at_utc", MaximumCachedTicksPerAccount);
        if (removedItems > 0)
        {
            // Once cached points are evicted, no old range may continue claiming complete coverage.
            await DeleteMarketRowsAsync(connection, transaction, "market_data_ranges", accountKey, cancellationToken);
            return;
        }
        await TrimTableAsync("market_data_ranges", "updated_at_utc", MaximumMarketRangesPerAccount);

        async Task<int> TrimTableAsync(string table, string timeColumn, int maximumRows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {table}
                WHERE rowid IN (
                    SELECT rowid FROM {table}
                    WHERE account_key=$account
                    ORDER BY {timeColumn} DESC, rowid DESC
                    LIMIT -1 OFFSET $maximum
                );
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$maximum", maximumRows);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> DeleteMarketRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE account_key=$account;";
        command.Parameters.AddWithValue("$account", accountKey);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddPositionPnlSampleAsync(PositionPnlSample sample, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO position_pnl_samples(account_key, position_id, captured_at_utc, payload_json)
            VALUES ($account, $position, $captured, $payload);
            """;
        command.Parameters.AddWithValue("$account", sample.TradeKey.AccountKey);
        command.Parameters.AddWithValue("$position", sample.TradeKey.PositionId);
        command.Parameters.AddWithValue("$captured", Format(sample.CapturedAtUtc));
        command.Parameters.AddWithValue("$payload", Serialize(sample));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddBehaviorOccurrenceAsync(BehaviorOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        if (occurrence.TradeLinks.Any(item => item.TradeKey.AccountKey != occurrence.AccountKey))
        {
            throw new ArgumentException("行为证据不能关联其他账户的交易。", nameof(occurrence));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = occurrence;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = (SqliteTransaction)transaction;
            current.CommandText = "SELECT payload_json FROM behavior_occurrences WHERE account_key=$account AND id=$id;";
            current.Parameters.AddWithValue("$account", occurrence.AccountKey);
            current.Parameters.AddWithValue("$id", occurrence.Id);
            var existing = DeserializeOrNull<BehaviorOccurrence>(
                await current.ExecuteScalarAsync(cancellationToken) as string);
            if (existing is not null)
            {
                stored = occurrence with
                {
                    UserExplanation = existing.UserExplanation,
                    EvidenceInsufficient = existing.EvidenceInsufficient,
                    Revision = existing.Revision,
                    HumanReviewedAtUtc = existing.HumanReviewedAtUtc,
                };
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO behavior_occurrences(id, account_key, server_date, rule, event_at_utc, payload_json)
                VALUES ($id, $account, $date, $rule, $event, $payload);
                """;
            command.Parameters.AddWithValue("$id", occurrence.Id);
            command.Parameters.AddWithValue("$account", occurrence.AccountKey);
            command.Parameters.AddWithValue("$date", Format(occurrence.ServerDate));
            command.Parameters.AddWithValue("$rule", occurrence.Rule.ToString());
            command.Parameters.AddWithValue("$event", Format(occurrence.EventAtUtc));
            command.Parameters.AddWithValue("$payload", Serialize(stored));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM behavior_trade_links WHERE account_key=$account AND occurrence_id=$id;";
            delete.Parameters.AddWithValue("$account", occurrence.AccountKey);
            delete.Parameters.AddWithValue("$id", occurrence.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var link in occurrence.TradeLinks)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO behavior_trade_links(account_key, occurrence_id, position_id, role) VALUES ($account, $id, $position, $role);";
            command.Parameters.AddWithValue("$account", occurrence.AccountKey);
            command.Parameters.AddWithValue("$id", occurrence.Id);
            command.Parameters.AddWithValue("$position", link.TradeKey.PositionId);
            command.Parameters.AddWithValue("$role", link.Role.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, occurrence.AccountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveBehaviorOccurrenceAsync(BehaviorOccurrence occurrence, CancellationToken cancellationToken = default) =>
        AddBehaviorOccurrenceAsync(occurrence, cancellationToken);

    public async Task<ReviewSaveResult<BehaviorOccurrence>> SaveBehaviorReviewAsync(
        string accountKey,
        string occurrenceId,
        string? userExplanation,
        bool evidenceInsufficient,
        int expectedRevision,
        DateTimeOffset reviewedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        BehaviorOccurrence? current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT payload_json FROM behavior_occurrences WHERE account_key=$account AND id=$id;";
            read.Parameters.AddWithValue("$account", accountKey);
            read.Parameters.AddWithValue("$id", occurrenceId);
            current = DeserializeOrNull<BehaviorOccurrence>(await read.ExecuteScalarAsync(cancellationToken) as string);
        }
        if (current is null)
        {
            return ReviewSaveResult<BehaviorOccurrence>.Missing("行为事件不存在或不属于当前账户。");
        }
        if (current.Revision != expectedRevision)
        {
            return ReviewSaveResult<BehaviorOccurrence>.Conflict("行为事件解释已被其他操作更新，请刷新后重试。");
        }

        var saved = current with
        {
            UserExplanation = userExplanation,
            EvidenceInsufficient = evidenceInsufficient,
            Revision = expectedRevision + 1,
            HumanReviewedAtUtc = reviewedAtUtc,
        };
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE behavior_occurrences SET payload_json=$payload WHERE account_key=$account AND id=$id;";
            update.Parameters.AddWithValue("$payload", Serialize(saved));
            update.Parameters.AddWithValue("$account", accountKey);
            update.Parameters.AddWithValue("$id", occurrenceId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return ReviewSaveResult<BehaviorOccurrence>.Conflict("行为事件在保存期间发生变化，请刷新后重试。");
            }
        }
        await BumpReviewVersionAsync(
            connection, (SqliteTransaction)transaction, accountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<BehaviorOccurrence>.Saved(saved);
    }

    public async Task SaveGoalObservationAsync(GoalObservation observation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO goal_observations(id, goal_id, account_key, server_date, payload_json)
            VALUES ($id, $goal, $account, $date, $payload);
            """;
        command.Parameters.AddWithValue("$id", observation.Id);
        command.Parameters.AddWithValue("$goal", observation.GoalId);
        command.Parameters.AddWithValue("$account", observation.AccountKey);
        command.Parameters.AddWithValue("$date", Format(observation.ServerDate));
        command.Parameters.AddWithValue("$payload", Serialize(observation));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, observation.AccountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReviewAttachment> SaveAttachmentAsync(ReviewAttachment attachment, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string attachmentId = attachment.Id;
        await using (var asset = connection.CreateCommand())
        {
            asset.Transaction = (SqliteTransaction)transaction;
            asset.CommandText = """
                INSERT OR IGNORE INTO attachment_assets(id, account_key, sha256, file_name, media_type, size_bytes, relative_path, created_at_utc)
                VALUES ($id, $account, $hash, $file, $media, $size, $path, $created);
                """;
            asset.Parameters.AddWithValue("$id", attachment.Id);
            asset.Parameters.AddWithValue("$account", attachment.AccountKey);
            asset.Parameters.AddWithValue("$hash", attachment.Sha256);
            asset.Parameters.AddWithValue("$file", attachment.FileName);
            asset.Parameters.AddWithValue("$media", attachment.MediaType);
            asset.Parameters.AddWithValue("$size", attachment.SizeBytes);
            asset.Parameters.AddWithValue("$path", attachment.RelativePath);
            asset.Parameters.AddWithValue("$created", Format(attachment.CreatedAtUtc));
            await asset.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = (SqliteTransaction)transaction;
            existing.CommandText = "SELECT id, file_name, media_type, size_bytes, relative_path, created_at_utc FROM attachment_assets WHERE account_key=$account AND sha256=$hash;";
            existing.Parameters.AddWithValue("$account", attachment.AccountKey);
            existing.Parameters.AddWithValue("$hash", attachment.Sha256);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("附件资产写入失败。");
            }
            attachmentId = reader.GetString(0);
            attachment = attachment with
            {
                Id = attachmentId,
                FileName = reader.GetString(1),
                MediaType = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                RelativePath = reader.GetString(4),
                CreatedAtUtc = ParseTimestamp(reader.GetString(5)),
            };
        }

        var saved = attachment with { Id = attachmentId };
        await using (var link = connection.CreateCommand())
        {
            link.Transaction = (SqliteTransaction)transaction;
            link.CommandText = """
                INSERT OR REPLACE INTO attachment_links(attachment_id, account_key, owner_kind, owner_id, title, evidence_json, event_reference)
                VALUES ($id, $account, $kind, $owner, $title, $evidence, $event);
                """;
            link.Parameters.AddWithValue("$id", saved.Id);
            link.Parameters.AddWithValue("$account", saved.AccountKey);
            link.Parameters.AddWithValue("$kind", saved.OwnerKind);
            link.Parameters.AddWithValue("$owner", saved.OwnerId);
            link.Parameters.AddWithValue("$title", saved.Title);
            link.Parameters.AddWithValue("$evidence", Serialize(saved.Evidence));
            link.Parameters.AddWithValue("$event", saved.EventReference.Trim());
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, saved.AccountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<string?> DeleteAttachmentLinkAsync(
        string attachmentId,
        string accountKey,
        string ownerKind,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? relativePath;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText = "SELECT relative_path FROM attachment_assets WHERE id=$id AND account_key=$account;";
            lookup.Parameters.AddWithValue("$id", attachmentId);
            lookup.Parameters.AddWithValue("$account", accountKey);
            relativePath = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }
        var linkDeleted = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM attachment_links WHERE attachment_id=$id AND account_key=$account AND owner_kind=$kind AND owner_id=$owner;";
            command.Parameters.AddWithValue("$id", attachmentId);
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$kind", ownerKind);
            command.Parameters.AddWithValue("$owner", ownerId);
            linkDeleted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        if (!linkDeleted)
        {
            return null;
        }

        var assetDeleted = false;
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = (SqliteTransaction)transaction;
            cleanup.CommandText = "DELETE FROM attachment_assets WHERE id=$id AND account_key=$account AND NOT EXISTS (SELECT 1 FROM attachment_links WHERE attachment_id=$id);";
            cleanup.Parameters.AddWithValue("$id", attachmentId);
            cleanup.Parameters.AddWithValue("$account", accountKey);
            assetDeleted = await cleanup.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, accountKey, "observation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assetDeleted ? relativePath : null;
    }

    private async Task<ReviewSaveResult<T>> SaveSimpleRevisionedAsync<T>(
        string table,
        string entityKind,
        string accountKey,
        string id,
        int expectedRevision,
        DateTimeOffset updatedAtUtc,
        T value,
        Func<T, int, T> withRevision,
        string extraColumn,
        object extraValue,
        CancellationToken cancellationToken,
        params (string Column, object Value)[] additionalColumns)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadRevisionAsync(connection, (SqliteTransaction)transaction,
            $"SELECT revision FROM {table} WHERE account_key=$account AND id=$id;", accountKey, id, cancellationToken);
        if (current != expectedRevision)
        {
            return ReviewSaveResult<T>.Conflict($"{entityKind} 已被修改，请刷新后重试。");
        }

        var saved = withRevision(value, current + 1);
        var columns = string.Join(", ", additionalColumns.Select(item => item.Column));
        var parameters = string.Join(", ", additionalColumns.Select((_, index) => $"$extra{index}"));
        var updateColumns = string.Join(", ", additionalColumns.Select(item => $"{item.Column}=excluded.{item.Column}"));
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"""
            INSERT INTO {table}(id, account_key, {extraColumn}, revision, updated_at_utc, payload_json{(columns.Length == 0 ? "" : ", " + columns)})
            VALUES ($id, $account, $extra, $revision, $updated, $payload{(parameters.Length == 0 ? "" : ", " + parameters)})
            ON CONFLICT(account_key, id) DO UPDATE SET {extraColumn}=excluded.{extraColumn}, revision=excluded.revision,
                updated_at_utc=excluded.updated_at_utc, payload_json=excluded.payload_json{(updateColumns.Length == 0 ? "" : ", " + updateColumns)};
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$extra", extraValue);
        command.Parameters.AddWithValue("$revision", current + 1);
        command.Parameters.AddWithValue("$updated", Format(updatedAtUtc));
        command.Parameters.AddWithValue("$payload", Serialize(saved));
        for (var index = 0; index < additionalColumns.Length; index++)
        {
            command.Parameters.AddWithValue($"$extra{index}", additionalColumns[index].Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SaveRevisionAsync(connection, (SqliteTransaction)transaction, entityKind, accountKey, id,
            current + 1, saved, updatedAtUtc, cancellationToken);
        await BumpReviewVersionAsync(connection, (SqliteTransaction)transaction, accountKey, "metadata", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewSaveResult<T>.Saved(saved);
    }

    private static async Task<int> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string accountKey,
        object id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task SaveRevisionAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityKind,
        string accountKey,
        string entityId,
        int revision,
        T value,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO review_revisions(entity_kind, account_key, entity_id, revision, payload_json, recorded_at_utc)
            VALUES ($kind, $account, $id, $revision, $payload, $recorded);
            """;
        command.Parameters.AddWithValue("$kind", entityKind);
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$id", entityId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$payload", Serialize(value));
        command.Parameters.AddWithValue("$recorded", Format(recordedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BumpReviewVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountKey,
        string component,
        CancellationToken cancellationToken,
        string? explicitValue = null)
    {
        var column = component switch
        {
            "source" => "source_version",
            "metadata" => "metadata_version",
            "observation" => "observation_version",
            "rule" => "rule_version",
            "time" => "time_version",
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = component is "rule" or "time"
            ? $"""
                INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
                VALUES ($account, 0, 0, 0, $rule, $time, $updated)
                ON CONFLICT(account_key) DO UPDATE SET {column}=$value, updated_at_utc=excluded.updated_at_utc;
                """
            : $"""
                INSERT INTO review_data_versions(account_key, source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc)
                VALUES ($account, 0, 0, 0, '', '', $updated)
                ON CONFLICT(account_key) DO UPDATE SET {column}={column}+1, updated_at_utc=excluded.updated_at_utc;
                """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        if (component is "rule" or "time")
        {
            command.Parameters.AddWithValue("$rule", component == "rule" ? explicitValue ?? string.Empty : string.Empty);
            command.Parameters.AddWithValue("$time", component == "time" ? explicitValue ?? string.Empty : string.Empty);
            command.Parameters.AddWithValue("$value", explicitValue ?? string.Empty);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteReadSnapshotCommandAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryRollbackReadSnapshotAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteReadSnapshotCommandAsync(connection, "ROLLBACK;", CancellationToken.None);
        }
        catch
        {
        }
    }

    private Task<SqliteConnection> CreateIsolatedReadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = $"TradePetExport-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            try
            {
                snapshot.Open();
                using var source = new SqliteConnection(_connectionString);
                source.Open();
                source.BackupDatabase(snapshot);
                cancellationToken.ThrowIfCancellationRequested();
                return snapshot;
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }, cancellationToken);

    private static async Task<IReadOnlyList<TradeRecord>> LoadSnapshotTradesAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position_id, symbol, side, opened_at_utc, closed_at_utc, open_server_date,
                   close_server_date, entry_price, exit_price, opening_volume, maximum_volume,
                   remaining_volume, net_pnl, is_complete
            FROM trades
            WHERE account_key = $account AND is_complete = 1
              AND open_server_date <= $to AND close_server_date >= $from
            UNION ALL
            SELECT position_id, symbol, side, opened_at_utc, closed_at_utc, open_server_date,
                   close_server_date, entry_price, exit_price, opening_volume, maximum_volume,
                   remaining_volume, net_pnl, is_complete
            FROM trades
            WHERE account_key = $account AND is_complete = 0 AND open_server_date <= $to
            ORDER BY opened_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var result = new List<TradeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTrade(reader, accountKey));
        }
        return result;
    }

    private static async Task<TradeRecord?> LoadSnapshotTradeAsync(
        SqliteConnection connection,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position_id, symbol, side, opened_at_utc, closed_at_utc, open_server_date,
                   close_server_date, entry_price, exit_price, opening_volume, maximum_volume,
                   remaining_volume, net_pnl, is_complete
            FROM trades
            WHERE account_key = $account AND position_id = $position;
            """;
        command.Parameters.AddWithValue("$account", key.AccountKey);
        command.Parameters.AddWithValue("$position", key.PositionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTrade(reader, key.AccountKey) : null;
    }

    private static TradeRecord ReadTrade(SqliteDataReader reader, string accountKey) =>
        new(
            accountKey, reader.GetInt64(0), reader.GetString(1), Enum.Parse<TradeSide>(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetDecimal(7), NullableDecimal(reader, 8), reader.GetDecimal(9), reader.GetDecimal(10),
            reader.GetDecimal(11), reader.GetDecimal(12), reader.GetInt32(13) == 1);

    private static async Task<IReadOnlyList<DealRecord>> LoadSnapshotDealsAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var dealFrom = from == DateOnly.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dealTo = to.DayNumber >= DateOnly.MaxValue.DayNumber - 1
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.ticket, d.order_ticket, d.position_id, d.symbol, d.side, d.entry_kind,
                   d.volume, d.price, d.profit, d.commission, d.swap, d.fee, d.occurred_at_utc
            FROM deals d
            WHERE d.account_key = $account
              AND (d.occurred_at_utc >= $dealFrom AND d.occurred_at_utc < $dealTo
                   OR EXISTS (
                       SELECT 1 FROM trades t
                       WHERE t.account_key = d.account_key AND t.position_id = d.position_id
                         AND ((t.is_complete = 1 AND t.open_server_date <= $to AND t.close_server_date >= $from)
                              OR (t.is_complete = 0 AND t.open_server_date <= $to))))
            ORDER BY d.occurred_at_utc, d.ticket;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        command.Parameters.AddWithValue("$dealFrom", Format(dealFrom));
        command.Parameters.AddWithValue("$dealTo", Format(dealTo));
        return await ReadDealsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<DealRecord>> LoadSnapshotDealsAsync(
        SqliteConnection connection,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticket, order_ticket, position_id, symbol, side, entry_kind, volume, price,
                   profit, commission, swap, fee, occurred_at_utc
            FROM deals
            WHERE account_key = $account AND position_id = $position
            ORDER BY occurred_at_utc, ticket;
            """;
        command.Parameters.AddWithValue("$account", key.AccountKey);
        command.Parameters.AddWithValue("$position", key.PositionId);
        return await ReadDealsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<DealRecord>> ReadDealsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<DealRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DealRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                Enum.Parse<TradeSide>(reader.GetString(4)), Enum.Parse<DealEntryKind>(reader.GetString(5)),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetDecimal(11), ParseTimestamp(reader.GetString(12))));
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<long, TradeReviewMetadata>> LoadSnapshotMetadataAsync(
        SqliteConnection connection,
        string accountKey,
        IReadOnlyCollection<long> positionIds,
        CancellationToken cancellationToken)
    {
        if (positionIds.Count == 0)
        {
            return new Dictionary<long, TradeReviewMetadata>();
        }
        var wanted = positionIds.ToHashSet();
        await using var command = connection.CreateCommand();
        var where = "account_key = $account";
        if (positionIds.Count <= 900)
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }
        command.CommandText = $"""
            SELECT position_id, plan_id, compliance_status, strategy, setup, tags_json, user_edited, updated_at_utc
            FROM trade_review_metadata WHERE {where};
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new Dictionary<long, TradeReviewMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var positionId = reader.GetInt64(0);
            if (!wanted.Contains(positionId))
            {
                continue;
            }
            result[positionId] = new TradeReviewMetadata(
                accountKey, positionId, reader.IsDBNull(1) ? null : reader.GetString(1),
                Enum.Parse<PlanComplianceStatus>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                DeserializeTags(reader.GetString(5)), reader.GetInt32(6) == 1, ParseTimestamp(reader.GetString(7)));
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<long, TradeExcursion>> LoadSnapshotExcursionsAsync(
        SqliteConnection connection,
        string accountKey,
        IReadOnlyCollection<long> positionIds,
        CancellationToken cancellationToken)
    {
        if (positionIds.Count == 0)
        {
            return new Dictionary<long, TradeExcursion>();
        }
        var wanted = positionIds.ToHashSet();
        await using var command = connection.CreateCommand();
        var where = "account_key = $account";
        if (positionIds.Count <= 900)
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }
        command.CommandText = $"""
            SELECT position_id, minimum_pnl, maximum_pnl, initial_risk_amount, planned_risk_multiple,
                   actual_risk_multiple, first_sample_at_utc, last_sample_at_utc,
                   covered_milliseconds, holding_milliseconds, started_at_open, is_complete,
                   maximum_gap_milliseconds, algorithm_version
            FROM trade_excursions WHERE {where};
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new Dictionary<long, TradeExcursion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var positionId = reader.GetInt64(0);
            if (!wanted.Contains(positionId))
            {
                continue;
            }
            result[positionId] = new TradeExcursion(
                accountKey, positionId, reader.GetDecimal(1), reader.GetDecimal(2), NullableDecimal(reader, 3),
                NullableDecimal(reader, 4), NullableDecimal(reader, 5), ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7)), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetInt32(10) == 1, reader.GetInt32(11) == 1,
                reader.GetInt64(12), reader.GetString(13));
        }
        return result;
    }

    private static async Task<IReadOnlyList<HistorySyncState>> LoadSnapshotHistoryStatesAsync(
        SqliteConnection connection,
        string accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT range_year, is_complete, deal_count, updated_at_utc
            FROM history_sync_state WHERE account_key = $account ORDER BY range_year DESC;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new List<HistorySyncState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HistorySyncState(
                accountKey, reader.GetInt32(0), reader.GetInt32(1) == 1, reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3))));
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<DateOnly, DailyState>> LoadSnapshotDailyStatesAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_date, daily_state_json FROM trading_days
            WHERE account_key = $account AND server_date BETWEEN $from AND $to ORDER BY server_date;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var result = new Dictionary<DateOnly, DailyState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var state = JsonSerializer.Deserialize<DailyState>(reader.GetString(1), ProtocolJson.Options);
            if (state is not null)
            {
                result[date] = state;
            }
        }
        return result;
    }

    private static async Task<IReadOnlyList<EquitySample>> LoadSnapshotEquitySamplesAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_date, captured_at_utc, balance, equity, floating_pnl, has_open_position
            FROM equity_samples
            WHERE account_key = $account AND server_date BETWEEN $from AND $to
            ORDER BY captured_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var result = new List<EquitySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EquitySample(
                accountKey,
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParseTimestamp(reader.GetString(1)), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetInt32(5) == 1));
        }
        return result;
    }

    private static async Task<IReadOnlyList<AccountCashFlow>> LoadSnapshotCashFlowsAsync(
        SqliteConnection connection,
        string accountKey,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticket, type, amount, occurred_at_utc FROM account_cash_flows
            WHERE account_key = $account AND occurred_at_utc >= $from AND occurred_at_utc < $to
            ORDER BY occurred_at_utc, ticket;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromUtc));
        command.Parameters.AddWithValue("$to", Format(toUtc));
        var result = new List<AccountCashFlow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccountCashFlow(
                accountKey, reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2),
                ParseTimestamp(reader.GetString(3))));
        }
        return result;
    }

    private static async Task<TradeDetailData?> LoadTradeDetailFromSnapshotAsync(
        SqliteConnection connection,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        var trade = await LoadSnapshotTradeAsync(connection, key, cancellationToken);
        if (trade is null)
        {
            return null;
        }
        var deals = await LoadSnapshotDealsAsync(connection, key, cancellationToken);
        var metadata = (await LoadSnapshotMetadataAsync(connection, key.AccountKey, [key.PositionId], cancellationToken))
            .GetValueOrDefault(key.PositionId);
        var excursion = (await LoadSnapshotExcursionsAsync(connection, key.AccountKey, [key.PositionId], cancellationToken))
            .GetValueOrDefault(key.PositionId);
        var document = await LoadTradeReviewDocumentAsync(connection, key, cancellationToken);
        var samples = await LoadJsonListForTradeAsync<PositionPnlSample>(
            connection, "position_pnl_samples", key, cancellationToken);
        var assessments = await LoadAssessmentsAsync(connection, key.AccountKey, [key.PositionId], cancellationToken);
        var behaviors = await LoadBehaviorsForTradeAsync(connection, key, cancellationToken);
        var attachments = await LoadAttachmentsAsync(
            connection, key.AccountKey, "trade", key.PositionId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var campaign = await LoadCampaignForTradeAsync(connection, key, cancellationToken);
        var version = await LoadReviewVersionAsync(connection, key.AccountKey, cancellationToken);
        var plan = string.IsNullOrWhiteSpace(metadata?.PlanId)
            ? null
            : await LoadStructuredTradePlanByIdAsync(connection, key.AccountKey, metadata.PlanId, cancellationToken);
        PlaybookVersion? playbook = null;
        var playbookId = assessments.Select(item => item.PlaybookVersionId).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(playbookId))
        {
            playbook = await LoadJsonSingleAsync<PlaybookVersion>(connection,
                "SELECT payload_json FROM playbook_versions WHERE account_key = $account AND id = $id;",
                key.AccountKey, playbookId, cancellationToken);
        }
        return new TradeDetailData(trade, deals, metadata, document, excursion, samples, plan, playbook,
            assessments, behaviors, attachments, campaign, version);
    }

    private static async Task<StructuredTradePlan?> LoadStructuredTradePlanByIdAsync(
        SqliteConnection connection,
        string accountKey,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, server_date, symbol, side, reference_entry_price, entry_low, entry_high,
                   stop_price, target_price, strategy, setup, tags_json, notes, is_active,
                   created_at_utc, updated_at_utc
            FROM structured_trade_plans
            WHERE account_key = $account AND id = $id;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$id", planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new StructuredTradePlan(
            reader.GetString(0), accountKey,
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetString(2), Enum.Parse<TradeSide>(reader.GetString(3)),
            NullableDecimal(reader, 4), NullableDecimal(reader, 5), NullableDecimal(reader, 6),
            NullableDecimal(reader, 7), NullableDecimal(reader, 8), reader.GetString(9), reader.GetString(10),
            DeserializeTags(reader.GetString(11)), reader.GetString(12), reader.GetInt32(13) == 1,
            ParseTimestamp(reader.GetString(14)), ParseTimestamp(reader.GetString(15)));
    }

    private static async Task<string> LoadCurrencyAsync(SqliteConnection connection, string accountKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT currency FROM accounts WHERE account_key=$account;";
        command.Parameters.AddWithValue("$account", accountKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
    }

    private static async Task<TradeReviewDocument?> LoadTradeReviewDocumentAsync(
        SqliteConnection connection,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM trade_review_documents WHERE account_key=$account AND position_id=$position;";
        command.Parameters.AddWithValue("$account", key.AccountKey);
        command.Parameters.AddWithValue("$position", key.PositionId);
        return DeserializeOrNull<TradeReviewDocument>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    private static async Task<IReadOnlyDictionary<long, TradeReviewDocument>> LoadDocumentsAsync(
        SqliteConnection connection,
        string accountKey,
        IReadOnlyCollection<long> positionIds,
        CancellationToken cancellationToken)
    {
        if (positionIds.Count == 0)
        {
            return new Dictionary<long, TradeReviewDocument>();
        }
        await using var command = connection.CreateCommand();
        var where = "account_key=$account";
        if (positionIds.Count <= 900)
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }
        command.CommandText = $"SELECT payload_json FROM trade_review_documents WHERE {where};";
        command.Parameters.AddWithValue("$account", accountKey);
        var items = await ReadJsonAsync<TradeReviewDocument>(command, cancellationToken);
        var wanted = positionIds.ToHashSet();
        return items.Where(item => wanted.Contains(item.TradeKey.PositionId)).ToDictionary(item => item.TradeKey.PositionId);
    }

    private static async Task<IReadOnlyDictionary<DateOnly, DailyJournal>> LoadDailyJournalsAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM daily_journals WHERE account_key=$account AND server_date BETWEEN $from AND $to;";
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var items = await ReadJsonAsync<DailyJournal>(command, cancellationToken);
        return items.ToDictionary(item => item.ServerDate);
    }

    private static async Task<IReadOnlyList<TradeRuleAssessment>> LoadAssessmentsAsync(
        SqliteConnection connection,
        string accountKey,
        IReadOnlyCollection<long> positionIds,
        CancellationToken cancellationToken)
    {
        if (positionIds.Count == 0)
        {
            return [];
        }
        await using var command = connection.CreateCommand();
        var where = "account_key=$account";
        if (positionIds.Count <= 900)
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }
        command.CommandText = $"SELECT payload_json FROM trade_rule_assessments WHERE {where} ORDER BY updated_at_utc;";
        command.Parameters.AddWithValue("$account", accountKey);
        var all = await ReadJsonAsync<TradeRuleAssessment>(command, cancellationToken);
        var wanted = positionIds.ToHashSet();
        return all.Where(item => wanted.Contains(item.TradeKey.PositionId)).ToArray();
    }

    private static async Task<IReadOnlyList<BehaviorOccurrence>> LoadBehaviorsAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM behavior_occurrences WHERE account_key=$account AND server_date BETWEEN $from AND $to ORDER BY event_at_utc;";
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        var persisted = await ReadJsonAsync<BehaviorOccurrence>(command, cancellationToken);

        await using var legacy = connection.CreateCommand();
        legacy.CommandText = """
            SELECT id, server_date, position_id, rule, value, baseline, threshold, level, triggered, summary, observed_at_utc
            FROM behavior_evaluations WHERE account_key=$account AND server_date BETWEEN $from AND $to ORDER BY observed_at_utc;
            """;
        legacy.Parameters.AddWithValue("$account", accountKey);
        legacy.Parameters.AddWithValue("$from", Format(from));
        legacy.Parameters.AddWithValue("$to", Format(to));
        var converted = new List<BehaviorOccurrence>();
        await using var reader = await legacy.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (persisted.Any(item => item.Id == id))
            {
                continue;
            }
            var date = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var observed = ParseTimestamp(reader.GetString(10));
            var links = reader.IsDBNull(2)
                ? Array.Empty<BehaviorTradeLink>()
                : [new BehaviorTradeLink(new TradeKey(accountKey, reader.GetInt64(2)), BehaviorTradeRole.Trigger)];
            converted.Add(new BehaviorOccurrence(
                id, accountKey, date, Enum.Parse<BehaviorRuleKind>(reader.GetString(3)), "legacy-v1",
                reader.GetDecimal(4), NullableDecimal(reader, 5), reader.GetDecimal(6),
                Enum.Parse<BehaviorRiskLevel>(reader.GetString(7)), ReviewEvidenceSource.RuleRecalculation,
                observed, observed, reader.GetString(9), reader.GetInt32(8) == 1 ? string.Empty : "规则未触发",
                false, "LegacyRecord", null, links));
        }
        return persisted.Concat(converted).OrderBy(item => item.EventAtUtc).ToArray();
    }

    private static async Task<IReadOnlyList<BehaviorOccurrence>> LoadBehaviorsForTradeAsync(
        SqliteConnection connection,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        var all = await LoadBehaviorsAsync(connection, key.AccountKey, new DateOnly(1970, 1, 1), new DateOnly(9999, 12, 31), cancellationToken);
        return all.Where(item => item.TradeLinks.Any(link => link.TradeKey == key)).ToArray();
    }

    private static async Task<IReadOnlyList<ReviewAttachment>> LoadAttachmentsAsync(
        SqliteConnection connection,
        string accountKey,
        string ownerKind,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.sha256, a.file_name, a.media_type, a.size_bytes, a.relative_path,
                   l.title, l.evidence_json, a.created_at_utc, l.event_reference
            FROM attachment_assets a INNER JOIN attachment_links l ON l.attachment_id=a.id
            WHERE l.account_key=$account AND l.owner_kind=$kind AND l.owner_id=$owner;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$kind", ownerKind);
        command.Parameters.AddWithValue("$owner", ownerId);
        var result = new List<ReviewAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReviewAttachment(reader.GetString(0), accountKey, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), ownerKind, ownerId,
                Deserialize<ReviewEvidenceStamp>(reader.GetString(7)), ParseTimestamp(reader.GetString(8)), reader.GetString(9)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ReviewAttachment>> LoadAttachmentsForKindAsync(
        SqliteConnection connection,
        string accountKey,
        string ownerKind,
        IReadOnlyCollection<string> ownerIds,
        CancellationToken cancellationToken)
    {
        if (ownerIds.Count == 0)
        {
            return [];
        }
        var ownerSet = ownerIds.ToHashSet(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        var ownerPredicate = string.Empty;
        if (ownerIds.Count <= 900)
        {
            var parameters = ownerIds.Select((ownerId, index) => ($"$owner{index}", ownerId)).ToArray();
            ownerPredicate = $" AND l.owner_id IN ({string.Join(", ", parameters.Select(item => item.Item1))})";
            foreach (var (name, ownerId) in parameters)
            {
                command.Parameters.AddWithValue(name, ownerId);
            }
        }
        command.CommandText = """
            SELECT a.id, a.sha256, a.file_name, a.media_type, a.size_bytes, a.relative_path,
                   l.title, l.owner_id, l.evidence_json, a.created_at_utc, l.event_reference
            FROM attachment_assets a INNER JOIN attachment_links l ON l.attachment_id=a.id
            WHERE l.account_key=$account AND l.owner_kind=$kind
            """ + ownerPredicate + """
            ORDER BY a.created_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$kind", ownerKind);
        var result = new List<ReviewAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!ownerSet.Contains(reader.GetString(7)))
            {
                continue;
            }
            result.Add(new ReviewAttachment(reader.GetString(0), accountKey, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), ownerKind,
                reader.GetString(7), Deserialize<ReviewEvidenceStamp>(reader.GetString(8)), ParseTimestamp(reader.GetString(9)),
                reader.GetString(10)));
        }
        return result;
    }

    private static async Task<TradeCampaign?> LoadCampaignForTradeAsync(SqliteConnection connection, TradeKey key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.payload_json FROM trade_campaigns c
            INNER JOIN trade_campaign_members m ON m.account_key=c.account_key AND m.campaign_id=c.id
            WHERE m.account_key=$account AND m.position_id=$position LIMIT 1;
            """;
        command.Parameters.AddWithValue("$account", key.AccountKey);
        command.Parameters.AddWithValue("$position", key.PositionId);
        return DeserializeOrNull<TradeCampaign>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    private static async Task<IReadOnlyList<MarketDataRange>> LoadMarketRangesAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM market_data_ranges WHERE account_key=$account
                AND requested_to_utc >= $from AND requested_from_utc < $to ORDER BY requested_from_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
        var toUtc = to == DateOnly.MaxValue
            ? new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero)
            : new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        command.Parameters.AddWithValue("$to", Format(toUtc));
        return await ReadJsonAsync<MarketDataRange>(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<PeriodReview>> LoadPeriodReviewsAsync(
        SqliteConnection connection,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM period_reviews WHERE account_key=$account
                AND to_server_date >= $from AND from_server_date <= $to ORDER BY updated_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        return await ReadJsonAsync<PeriodReview>(command, cancellationToken);
    }

    private static async Task<ReviewDataVersion> LoadReviewVersionAsync(
        SqliteConnection connection,
        string accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_version, metadata_version, observation_version, rule_version, time_version, updated_at_utc FROM review_data_versions WHERE account_key=$account;";
        command.Parameters.AddWithValue("$account", accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReviewDataVersion(accountKey, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), ParseTimestamp(reader.GetString(5)))
            : new ReviewDataVersion(accountKey, 0, 0, 0, string.Empty, string.Empty, DateTimeOffset.MinValue);
    }

    private static async Task<IReadOnlyList<T>> LoadDatedJsonAsync<T>(
        SqliteConnection connection,
        string table,
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE account_key=$account AND server_date BETWEEN $from AND $to ORDER BY server_date;";
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        return await ReadJsonAsync<T>(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> LoadJsonListForTradeAsync<T>(
        SqliteConnection connection,
        string table,
        TradeKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE account_key=$account AND position_id=$position ORDER BY captured_at_utc;";
        command.Parameters.AddWithValue("$account", key.AccountKey);
        command.Parameters.AddWithValue("$position", key.PositionId);
        return await ReadJsonAsync<T>(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> LoadJsonListAsync<T>(
        SqliteConnection connection,
        string sql,
        string accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", accountKey);
        return await ReadJsonAsync<T>(command, cancellationToken);
    }

    private static async Task<T?> LoadJsonSingleAsync<T>(
        SqliteConnection connection,
        string sql,
        string accountKey,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$id", id);
        return DeserializeOrNull<T>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonAsync<T>(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Deserialize<T>(reader.GetString(0)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<T>> LoadMarketItemsAsync<T>(
        SqliteConnection connection,
        string table,
        string accountKey,
        string terminalId,
        string symbol,
        string? timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var timeColumn = table == "market_bars" ? "opened_at_utc" : "occurred_at_utc";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT payload_json FROM {table}
            WHERE account_key=$account AND terminal_id=$terminal AND symbol=$symbol COLLATE NOCASE
                {(timeframe is null ? string.Empty : "AND timeframe=$timeframe")}
                AND {timeColumn} >= $from AND {timeColumn} <= $to ORDER BY {timeColumn};
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$terminal", terminalId);
        command.Parameters.AddWithValue("$symbol", symbol);
        if (timeframe is not null)
        {
            command.Parameters.AddWithValue("$timeframe", timeframe);
        }
        command.Parameters.AddWithValue("$from", Format(fromUtc));
        command.Parameters.AddWithValue("$to", Format(toUtc));
        return await ReadJsonAsync<T>(command, cancellationToken);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)
        ?? throw new InvalidDataException($"Stored {typeof(T).Name} payload is invalid.");

    private static T? DeserializeOrNull<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : Deserialize<T>(json);
}
