using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradePet.Core.Domain;
using TradePet.Core.Review;
using TradePet.Core.Trading;

namespace TradePet.Application.Review;

public sealed class ReviewQueryService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly ReviewWorkspaceCalculator _workspaceCalculator = new();
    private readonly ReviewAnalyticsCalculator _analyticsCalculator = new();
    private readonly object _cacheGate = new();
    private WorkspaceCacheEntry? _workspaceCache;

    public ReviewQueryService(IReviewWorkspaceRepository repository) =>
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<ReviewQueryResult> QueryAsync(
        ReviewQueryContext context,
        ReviewWorkspaceFilter filter,
        Func<long> currentSessionGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(filter);
        var currentVersion = await _repository.LoadReviewDataVersionAsync(filter.AccountKey, cancellationToken);
        ReviewWorkspaceData? data;
        lock (_cacheGate)
        {
            data = _workspaceCache is { } cached &&
                   cached.AccountKey == filter.AccountKey &&
                   cached.FromServerDate == filter.FromServerDate &&
                   cached.ToServerDate == filter.ToServerDate &&
                   cached.VersionToken == currentVersion.Token
                ? cached.Data
                : null;
        }
        if (data is null)
        {
            data = await _repository.LoadWorkspaceAsync(
                filter.AccountKey, filter.FromServerDate, filter.ToServerDate, cancellationToken);
            lock (_cacheGate)
            {
                _workspaceCache = new WorkspaceCacheEntry(
                    filter.AccountKey, filter.FromServerDate, filter.ToServerDate, data.Version.Token, data, []);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var filterCacheKey = JsonSerializer.Serialize(filter);
        ReviewWorkspaceSnapshot? cachedSnapshot;
        lock (_cacheGate)
        {
            cachedSnapshot = _workspaceCache is { Data: var cachedData } cache &&
                             ReferenceEquals(cachedData, data) &&
                             cache.Snapshots.TryGetValue(filterCacheKey, out var found)
                ? found
                : null;
        }
        if (cachedSnapshot is not null)
        {
            var cachedIsCurrent = currentSessionGeneration() == context.SessionGeneration &&
                                  string.Equals(context.ExpectedAccountKey, filter.AccountKey, StringComparison.Ordinal);
            return new ReviewQueryResult(context, cachedSnapshot, data, cachedIsCurrent);
        }
        var effectiveDocuments = _workspaceCalculator.ProjectReviewStatuses(
            data.Trades, data.Deals, data.Documents, data.Assessments, data.Excursions, data.Version);
        var selected = _workspaceCalculator.FilterAndSort(filter, data.Trades, data.Metadata, effectiveDocuments,
            data.Assessments, data.Campaigns, data.Behaviors, data.Deals, data.Excursions);
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var page = Math.Max(1, filter.Page);
        var paged = selected.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        var pagedIds = paged.Select(item => item.PositionId).ToHashSet();
        var analyticsFilter = new ReviewFilter(
            filter.AccountKey, filter.FromServerDate, filter.ToServerDate, filter.Symbol, filter.Side,
            filter.Strategy, filter.Setup, filter.Tags?.Count == 1 ? filter.Tags[0] : null,
            filter.ServerUtcOffsetSeconds);
        var selectedIds = selected.Select(item => item.PositionId).ToHashSet();
        var selectedMetadata = data.Metadata.Where(item => selectedIds.Contains(item.Key)).ToDictionary();
        var analytics = _analyticsCalculator.Calculate(
            analyticsFilter, selected, selectedMetadata, data.Excursions, deals: data.Deals);
        cancellationToken.ThrowIfCancellationRequested();
        var calendar = _workspaceCalculator.BuildCalendar(filter.AccountKey, filter.FromServerDate, filter.ToServerDate,
            data.Trades, data.Deals, effectiveDocuments, data.DailyJournals, data.DataGapDates, filter.ServerUtcOffsetSeconds);
        var dailyFacts = _workspaceCalculator.BuildDailyFacts(
            filter.AccountKey, filter.FromServerDate, filter.ToServerDate, data.Trades, data.Deals,
            effectiveDocuments, data.Behaviors, data.DailyStates ?? new Dictionary<DateOnly, DailyState>(),
            filter.ServerUtcOffsetSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        var quality = _workspaceCalculator.CalculateDataQuality(selected, data.Excursions, effectiveDocuments,
            data.Metadata, data.MarketRanges);
        var midpoint = filter.FromServerDate.AddDays(filter.FromServerDate.DayNumber <= filter.ToServerDate.DayNumber
            ? (filter.ToServerDate.DayNumber - filter.FromServerDate.DayNumber) / 2
            : 0);
        var comparisonGroups = filter.ComparisonMode switch
        {
            ReviewComparisonMode.Compliance => (
                "规则合规（全部适用规则已知且通过）",
                selected.Where(trade => IsCompliant(trade, data.Assessments)).ToArray(),
                "有违规（至少一项规则未通过）",
                selected.Where(trade => HasViolation(trade, data.Assessments)).ToArray()),
            ReviewComparisonMode.Side => (
                "方向=买入",
                selected.Where(item => item.Side == TradeSide.Buy).ToArray(),
                "方向=卖出",
                selected.Where(item => item.Side == TradeSide.Sell).ToArray()),
            _ => (
                $"平仓日 {filter.FromServerDate:yyyy-MM-dd} 至 {midpoint:yyyy-MM-dd}",
                selected.Where(item => item.CloseServerDate <= midpoint).ToArray(),
                $"平仓日 {midpoint.AddDays(1):yyyy-MM-dd} 至 {filter.ToServerDate:yyyy-MM-dd}",
                selected.Where(item => item.CloseServerDate > midpoint).ToArray()),
        };
        var comparison = _workspaceCalculator.Compare(
            comparisonGroups.Item1, comparisonGroups.Item2, comparisonGroups.Item3, comparisonGroups.Item4,
            data.Metadata, data.Excursions, filter.ServerUtcOffsetSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        var fees = _workspaceCalculator.CalculateFees(
            filter.AccountKey, filter.FromServerDate, filter.ToServerDate, selected, data.Trades,
            data.Deals, filter.ServerUtcOffsetSeconds);
        var dailyCash = _workspaceCalculator.BuildDailyCashSeries(
            filter.AccountKey, filter.FromServerDate, filter.ToServerDate, selected, data.Deals,
            filter.ServerUtcOffsetSeconds);
        var riskSamples = _workspaceCalculator.BuildRiskSamples(selected, data.Excursions);
        var selectedTrades = selected.ToDictionary(
            item => new TradeKey(item.AccountKey, item.PositionId), item => item);
        var behaviorEvidence = _workspaceCalculator.CompareBehaviorSamples(data.Behaviors, selectedTrades);
        var periodFacts = _workspaceCalculator.BuildPeriodFacts(
            selected, effectiveDocuments, data.Assessments, data.Behaviors, quality);
        var goalProgress = _workspaceCalculator.BuildGoalProgress(
            data.Goals, data.GoalObservations, filter.FromServerDate, filter.ToServerDate);
        var equityAnalysis = _workspaceCalculator.BuildEquityAnalysis(
            filter.AccountKey, filter.FromServerDate, filter.ToServerDate,
            data.EquitySamples ?? [], data.CashFlows ?? [], filter.ServerUtcOffsetSeconds);
        var sessionPerformance = _workspaceCalculator.BuildTradingSessionPerformance(
            selected, data.TradingSessions ?? [], data.Excursions, data.Deals);
        var snapshot = new ReviewWorkspaceSnapshot(
            filter, analytics, paged,
            effectiveDocuments.Where(item => pagedIds.Contains(item.Key)).ToDictionary(),
            calendar, _workspaceCalculator.BuildRealizedCurve(selected), data.Behaviors, quality,
            data.Version, selected.Count, page, pageSize, comparison, dailyFacts, fees, dailyCash, riskSamples,
            behaviorEvidence, periodFacts, goalProgress, equityAnalysis, sessionPerformance, selected);
        lock (_cacheGate)
        {
            if (_workspaceCache is { Data: var cachedData } cache && ReferenceEquals(cachedData, data))
            {
                if (!cache.Snapshots.ContainsKey(filterCacheKey) && cache.Snapshots.Count >= 32)
                {
                    cache.Snapshots.Remove(cache.Snapshots.Keys.First());
                }
                cache.Snapshots[filterCacheKey] = snapshot;
            }
        }
        var isCurrent = currentSessionGeneration() == context.SessionGeneration &&
                        string.Equals(context.ExpectedAccountKey, filter.AccountKey, StringComparison.Ordinal);
        return new ReviewQueryResult(context, snapshot, data, isCurrent);

        bool IsCompliant(TradeRecord trade, IReadOnlyCollection<TradeRuleAssessment> assessments)
        {
            var summary = _workspaceCalculator.SummarizeRules(assessments
                .Where(item => item.TradeKey == new TradeKey(trade.AccountKey, trade.PositionId)).ToArray());
            return summary.IsCompliant;
        }

        bool HasViolation(TradeRecord trade, IReadOnlyCollection<TradeRuleAssessment> assessments)
        {
            var summary = _workspaceCalculator.SummarizeRules(assessments
                .Where(item => item.TradeKey == new TradeKey(trade.AccountKey, trade.PositionId)).ToArray());
            return summary.HasViolation;
        }
    }

    private sealed record WorkspaceCacheEntry(
        string AccountKey,
        DateOnly FromServerDate,
        DateOnly ToServerDate,
        string VersionToken,
        ReviewWorkspaceData Data,
        Dictionary<string, ReviewWorkspaceSnapshot> Snapshots);
}

public sealed class JournalService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public JournalService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<TradeReviewDocument>> SaveTradeReviewAsync(
        SaveTradeReviewCommand command,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        await SaveTradeReviewCoreAsync(command, expectedRevision, cancellationToken);

    public async Task<EditSaveReceipt<TradeReviewDocument>> SaveTradeReviewAsync(
        TradeReviewEditSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!submission.Snapshot.Identity.IsForTrade(submission.Snapshot.Content.TradeKey))
        {
            return new EditSaveReceipt<TradeReviewDocument>(
                submission.Snapshot.Identity,
                submission.Snapshot.ContentSequence,
                EditSaveStatus.ValidationFailed,
                null,
                "编辑身份与目标交易不匹配，复盘草稿未保存。");
        }
        var result = await SaveTradeReviewCoreAsync(
            submission.Snapshot.Content,
            submission.ExpectedRevision,
            cancellationToken);
        var status = result.Status switch
        {
            ReviewSaveStatus.Saved => EditSaveStatus.Saved,
            ReviewSaveStatus.ValidationFailed => EditSaveStatus.ValidationFailed,
            ReviewSaveStatus.Conflict => EditSaveStatus.Conflict,
            ReviewSaveStatus.NotFound => EditSaveStatus.NotFound,
            _ => EditSaveStatus.StorageFailed,
        };
        return new EditSaveReceipt<TradeReviewDocument>(
            submission.Snapshot.Identity,
            submission.Snapshot.ContentSequence,
            status,
            result.Value,
            result.Message);
    }

    private async Task<ReviewSaveResult<TradeReviewDocument>> SaveTradeReviewCoreAsync(
        SaveTradeReviewCommand command,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var detail = await _repository.LoadTradeDetailAsync(command.TradeKey, cancellationToken);
        if (detail is null)
        {
            return ReviewSaveResult<TradeReviewDocument>.Missing("目标交易不存在，复盘草稿未保存。");
        }
        var actualKey = new TradeKey(detail.Trade.AccountKey, detail.Trade.PositionId);
        if (actualKey != command.TradeKey || detail.Document is not null && detail.Document.TradeKey != command.TradeKey)
        {
            return ReviewSaveResult<TradeReviewDocument>.Validation("交易事实或已有复盘的账户来源不匹配。");
        }
        if (!detail.Trade.IsComplete)
        {
            return ReviewSaveResult<TradeReviewDocument>.Validation("持仓中的交易不能创建完整交易复盘。");
        }

        var existing = detail.Document;
        var now = _timeProvider.GetUtcNow();
        var status = command.RequestedStatus == ReviewCompletionStatus.Reviewed
            ? ReviewCompletionStatus.Draft
            : command.RequestedStatus;
        var document = new TradeReviewDocument(
            command.TradeKey, status, command.EntryReason, command.ExitReason, command.DidWell,
            command.ToImprove, command.NextAction, command.Summary, command.Emotion, command.MarketCondition,
            expectedRevision + 1, command.SourceVersion, command.RuleVersion,
            existing?.ReviewedSourceVersion, existing?.ReviewedRuleVersion,
            existing?.CreatedAtUtc ?? now, now, existing?.ReviewedAtUtc).Normalize();
        return await _repository.SaveTradeReviewDocumentAsync(document, expectedRevision, cancellationToken);
    }

    public async Task<ReviewSaveResult<TradeReviewDocument>> MarkReviewedAsync(
        TradeKey key,
        string sourceVersion,
        string ruleVersion,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var detail = await _repository.LoadTradeDetailAsync(key, cancellationToken);
        if (detail?.Document is null)
        {
            return ReviewSaveResult<TradeReviewDocument>.Missing("请先保存交易复盘草稿。");
        }
        if (!detail.Trade.IsComplete)
        {
            return ReviewSaveResult<TradeReviewDocument>.Validation("持仓中的交易只能保存草稿。");
        }
        if (!detail.Document.HasRequiredReviewContent)
        {
            return ReviewSaveResult<TradeReviewDocument>.Validation("完成复盘需要总结和下一次执行动作。");
        }
        if (!detail.Assessments.Any(item => item.Status != RuleAssessmentStatus.NotApplicable))
        {
            return ReviewSaveResult<TradeReviewDocument>.Validation("完成复盘需要至少一项执行评价。");
        }
        var now = _timeProvider.GetUtcNow();
        var reviewed = detail.Document with
        {
            Status = ReviewCompletionStatus.Reviewed,
            Revision = expectedRevision + 1,
            SourceVersion = sourceVersion,
            RuleVersion = ruleVersion,
            ReviewedSourceVersion = sourceVersion,
            ReviewedRuleVersion = ruleVersion,
            ReviewedAtUtc = now,
            UpdatedAtUtc = now,
        };
        return await _repository.SaveTradeReviewDocumentAsync(reviewed, expectedRevision, cancellationToken);
    }

    public async Task<ReviewSaveResult<TradeReviewDocument>?> MarkNeedsReviewIfChangedAsync(
        TradeKey key,
        string sourceVersion,
        string ruleVersion,
        CancellationToken cancellationToken = default)
    {
        var document = await _repository.LoadTradeReviewDocumentAsync(key, cancellationToken);
        if (document is null || document.Status != ReviewCompletionStatus.Reviewed ||
            document.ReviewedSourceVersion == sourceVersion && document.ReviewedRuleVersion == ruleVersion)
        {
            return null;
        }
        var changed = document with
        {
            Status = ReviewCompletionStatus.NeedsReview,
            Revision = document.Revision + 1,
            SourceVersion = sourceVersion,
            RuleVersion = ruleVersion,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        return await _repository.SaveTradeReviewDocumentAsync(changed, document.Revision, cancellationToken);
    }

    public Task<ReviewSaveResult<DailyJournal>> SaveDailyJournalAsync(
        DailyJournal journal,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (journal.ServerDate == default ||
            string.IsNullOrWhiteSpace(journal.PreMarketPlan) &&
            string.IsNullOrWhiteSpace(journal.IntradayNotes) &&
            string.IsNullOrWhiteSpace(journal.PostMarketSummary) &&
            string.IsNullOrWhiteSpace(journal.NextAction))
        {
            return Task.FromResult(ReviewSaveResult<DailyJournal>.Validation("日记日期有效且至少需要填写一项内容。"));
        }
        var now = _timeProvider.GetUtcNow();
        var normalized = journal with
        {
            PreMarketPlan = journal.PreMarketPlan.Trim(),
            IntradayNotes = journal.IntradayNotes.Trim(),
            PostMarketSummary = journal.PostMarketSummary.Trim(),
            DidWell = journal.DidWell.Trim(),
            ToImprove = journal.ToImprove.Trim(),
            NextAction = journal.NextAction.Trim(),
            Status = ReviewCompletionStatus.Draft,
            Revision = expectedRevision + 1,
            CreatedAtUtc = journal.CreatedAtUtc == default ? now : journal.CreatedAtUtc,
            UpdatedAtUtc = now,
        };
        return _repository.SaveDailyJournalAsync(normalized, expectedRevision, cancellationToken);
    }

    public async Task<ReviewSaveResult<DailyJournal>> CompleteDailyJournalAsync(
        DailyJournal journal,
        int expectedRevision,
        string dailySourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (journal.ServerDate == default ||
            string.IsNullOrWhiteSpace(journal.PostMarketSummary) ||
            string.IsNullOrWhiteSpace(journal.DidWell) ||
            string.IsNullOrWhiteSpace(journal.ToImprove) ||
            string.IsNullOrWhiteSpace(journal.NextAction))
        {
            return ReviewSaveResult<DailyJournal>.Validation(
                "完成日总结需要填写执行事实、做对的动作、待改进行为和下一次检查事项。");
        }
        if (string.IsNullOrWhiteSpace(dailySourceVersion))
        {
            return ReviewSaveResult<DailyJournal>.Validation("完成日总结前需要刷新当日事实。" );
        }

        var now = _timeProvider.GetUtcNow();
        var sourceVersion = dailySourceVersion.Trim();
        var completed = journal with
        {
            PreMarketPlan = journal.PreMarketPlan.Trim(),
            IntradayNotes = journal.IntradayNotes.Trim(),
            PostMarketSummary = journal.PostMarketSummary.Trim(),
            DidWell = journal.DidWell.Trim(),
            ToImprove = journal.ToImprove.Trim(),
            NextAction = journal.NextAction.Trim(),
            Status = ReviewCompletionStatus.Reviewed,
            Revision = expectedRevision + 1,
            SourceVersion = sourceVersion,
            ReviewedSourceVersion = sourceVersion,
            CreatedAtUtc = journal.CreatedAtUtc == default ? now : journal.CreatedAtUtc,
            UpdatedAtUtc = now,
            ReviewedAtUtc = now,
        };
        return await _repository.SaveDailyJournalAsync(completed, expectedRevision, cancellationToken);
    }

    public async Task<ReviewSaveResult<DailyJournal>?> MarkDailyNeedsReviewIfChangedAsync(
        DailyJournal journal,
        string dailySourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (journal.Status != ReviewCompletionStatus.Reviewed)
        {
            return null;
        }

        var sourceVersion = dailySourceVersion.Trim();
        var reviewedSourceVersion = journal.ReviewedSourceVersion ?? journal.SourceVersion;
        if (string.IsNullOrWhiteSpace(sourceVersion) || reviewedSourceVersion == sourceVersion)
        {
            return null;
        }

        var changed = journal with
        {
            Status = ReviewCompletionStatus.NeedsReview,
            Revision = journal.Revision + 1,
            SourceVersion = sourceVersion,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        return await _repository.SaveDailyJournalAsync(changed, journal.Revision, cancellationToken);
    }

    public Task<ReviewSaveResult<PeriodReview>> SavePeriodReviewAsync(
        PeriodReview review,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (review.ToServerDate < review.FromServerDate || string.IsNullOrWhiteSpace(review.NextAction))
        {
            return Task.FromResult(ReviewSaveResult<PeriodReview>.Validation("周期范围必须有效，并填写下一次动作。"));
        }
        var now = _timeProvider.GetUtcNow();
        return _repository.SavePeriodReviewAsync(review with
        {
            Revision = expectedRevision + 1,
            CreatedAtUtc = review.CreatedAtUtc == default ? now : review.CreatedAtUtc,
            UpdatedAtUtc = now,
        }, expectedRevision, cancellationToken);
    }
}

public sealed class ReviewBulkEditService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ReviewWorkspaceCalculator _workspaceCalculator = new();

    public ReviewBulkEditService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<ReviewBulkEditResult>> ApplyAsync(
        ReviewBulkEditRequest request,
        CancellationToken cancellationToken = default)
    {
        var positionIds = request.PositionIds.Distinct().ToArray();
        if (string.IsNullOrWhiteSpace(request.AccountKey) || positionIds.Length == 0 || positionIds.Length > 100)
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Validation("批量操作需要选择 1–100 笔当前账户交易。");
        }
        var data = await _repository.LoadWorkspaceAsync(
            request.AccountKey, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        var trades = data.Trades.Where(item => positionIds.Contains(item.PositionId) &&
                                               item.AccountKey == request.AccountKey).ToArray();
        if (trades.Length != positionIds.Length || trades.Any(item => !item.IsComplete))
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Validation("批量操作包含不存在、跨账户或尚未完成的交易。");
        }

        var now = _timeProvider.GetUtcNow();
        var writes = new List<ReviewBulkWriteItem>(trades.Length);
        switch (request.Kind)
        {
            case ReviewBulkEditKind.Strategy:
                var strategy = request.Value.Trim();
                if (strategy.Length == 0)
                {
                    return ReviewSaveResult<ReviewBulkEditResult>.Validation("批量策略不能为空。");
                }
                foreach (var trade in trades)
                {
                    data.Metadata.TryGetValue(trade.PositionId, out var existing);
                    var metadata = (existing ?? EmptyMetadata(trade, now)) with
                    {
                        Strategy = strategy,
                        UserEdited = true,
                        UpdatedAtUtc = now,
                    };
                    writes.Add(new ReviewBulkWriteItem(
                        new TradeKey(request.AccountKey, trade.PositionId), metadata, existing?.UpdatedAtUtc, null, 0));
                }
                break;

            case ReviewBulkEditKind.Tags:
                var tags = SplitTags(request.Value);
                foreach (var trade in trades)
                {
                    data.Metadata.TryGetValue(trade.PositionId, out var existing);
                    var metadata = (existing ?? EmptyMetadata(trade, now)) with
                    {
                        Tags = tags,
                        UserEdited = true,
                        UpdatedAtUtc = now,
                    };
                    writes.Add(new ReviewBulkWriteItem(
                        new TradeKey(request.AccountKey, trade.PositionId), metadata, existing?.UpdatedAtUtc, null, 0));
                }
                break;

            case ReviewBulkEditKind.Status:
                if (!Enum.TryParse<ReviewCompletionStatus>(request.Value, true, out var status))
                {
                    return ReviewSaveResult<ReviewBulkEditResult>.Validation("批量复盘状态无效。");
                }
                foreach (var trade in trades)
                {
                    data.Documents.TryGetValue(trade.PositionId, out var existing);
                    var key = new TradeKey(request.AccountKey, trade.PositionId);
                    if (status == ReviewCompletionStatus.Reviewed &&
                        (existing is null || !existing.HasRequiredReviewContent ||
                         !data.Assessments.Any(item => item.TradeKey == key &&
                                                       item.Status != RuleAssessmentStatus.NotApplicable)))
                    {
                        return ReviewSaveResult<ReviewBulkEditResult>.Validation(
                            $"交易 #{trade.PositionId} 尚未填写总结、下一次动作或执行评价，不能批量标记已复盘。");
                    }
                    data.Excursions.TryGetValue(trade.PositionId, out var excursion);
                    var basis = _workspaceCalculator.BuildTradeReviewBasis(
                        trade, data.Deals, data.Assessments, excursion);
                    var document = (existing ?? EmptyDocument(key, basis.SourceVersion, basis.RuleVersion, now)) with
                    {
                        Status = status,
                        SourceVersion = basis.SourceVersion,
                        RuleVersion = basis.RuleVersion,
                        ReviewedSourceVersion = status == ReviewCompletionStatus.Reviewed ? basis.SourceVersion : existing?.ReviewedSourceVersion,
                        ReviewedRuleVersion = status == ReviewCompletionStatus.Reviewed ? basis.RuleVersion : existing?.ReviewedRuleVersion,
                        ReviewedAtUtc = status == ReviewCompletionStatus.Reviewed ? now : existing?.ReviewedAtUtc,
                        UpdatedAtUtc = now,
                    };
                    writes.Add(new ReviewBulkWriteItem(key, null, null, document, existing?.Revision ?? 0));
                }
                break;

            default:
                return ReviewSaveResult<ReviewBulkEditResult>.Validation("不支持的批量操作。");
        }

        return await _repository.SaveBulkReviewAsync(writes, cancellationToken);
    }

    private static TradeReviewMetadata EmptyMetadata(TradeRecord trade, DateTimeOffset now) =>
        new(trade.AccountKey, trade.PositionId, null, PlanComplianceStatus.Unclassified,
            string.Empty, string.Empty, [], true, now);

    private static TradeReviewDocument EmptyDocument(
        TradeKey key, string sourceVersion, string ruleVersion, DateTimeOffset now) =>
        new(key, ReviewCompletionStatus.Pending, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, 0, sourceVersion, ruleVersion,
            null, null, now, now, null);

    private static IReadOnlyList<string> SplitTags(string value) => value
        .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed class ReviewTagSuggestionService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ReviewTagSuggestionService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<ReviewBulkEditResult>> ApplyAsync(
        TradeKey key,
        string tag,
        bool accept,
        CancellationToken cancellationToken = default)
    {
        var normalizedTag = tag.Trim();
        if (string.IsNullOrWhiteSpace(key.AccountKey) || key.PositionId <= 0 ||
            normalizedTag.Length is 0 or > 64)
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Validation("候选标签必须属于当前交易，且长度不能超过 64 个字符。");
        }

        var detail = await _repository.LoadTradeDetailAsync(key, cancellationToken);
        if (detail is null)
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Missing("交易不存在或不属于当前账户。");
        }

        var current = detail.Metadata;
        var tags = (current?.Tags ?? [])
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingIndex = tags.FindIndex(item => string.Equals(item, normalizedTag, StringComparison.OrdinalIgnoreCase));
        if (accept && existingIndex < 0)
        {
            tags.Add(normalizedTag);
        }
        else if (!accept && existingIndex >= 0)
        {
            tags.RemoveAt(existingIndex);
        }
        else
        {
            return ReviewSaveResult<ReviewBulkEditResult>.Saved(new ReviewBulkEditResult(0));
        }

        var now = _timeProvider.GetUtcNow();
        var metadata = (current ?? new TradeReviewMetadata(
            key.AccountKey, key.PositionId, null, PlanComplianceStatus.Unclassified,
            string.Empty, string.Empty, [], true, now)) with
        {
            Tags = tags,
            UserEdited = true,
            UpdatedAtUtc = now,
        };
        return await _repository.SaveBulkReviewAsync(
            [new ReviewBulkWriteItem(key, metadata, current?.UpdatedAtUtc, null, 0)],
            cancellationToken);
    }
}

public sealed class PlaybookService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public PlaybookService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ReviewSaveResult<PlaybookVersion>> SaveVersionAsync(
        PlaybookVersion version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version.Name) || version.Rules.Count == 0 ||
            version.Rules.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != version.Rules.Count)
        {
            return Task.FromResult(ReviewSaveResult<PlaybookVersion>.Validation("策略模板需要名称、规则，且规则 ID 不能重复。"));
        }
        var normalized = version with
        {
            Name = version.Name.Trim(),
            Rules = version.Rules.OrderBy(item => item.Order).ToArray(),
            CreatedAtUtc = version.CreatedAtUtc == default ? _timeProvider.GetUtcNow() : version.CreatedAtUtc,
        };
        return _repository.SavePlaybookVersionAsync(normalized, cancellationToken);
    }
}

public sealed class CampaignService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public CampaignService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<TradeCampaign>> SaveAsync(
        TradeCampaign campaign,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var data = await _repository.LoadWorkspaceAsync(campaign.AccountKey, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        var memberIds = campaign.PositionIds.Distinct().ToArray();
        var members = data.Trades.Where(item => memberIds.Contains(item.PositionId)).ToArray();
        if (memberIds.Length == 0 || members.Length != memberIds.Length ||
            members.Any(item => item.AccountKey != campaign.AccountKey ||
                                !string.Equals(item.Symbol, campaign.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            return ReviewSaveResult<TradeCampaign>.Validation("交易想法成员必须来自同一账户和同一精确品种。");
        }
        var occupied = data.Campaigns.Where(item => item.Id != campaign.Id)
            .SelectMany(item => item.PositionIds).ToHashSet();
        if (memberIds.Any(occupied.Contains))
        {
            return ReviewSaveResult<TradeCampaign>.Validation("一笔交易不能同时属于两个有效交易想法。");
        }
        var now = _timeProvider.GetUtcNow();
        return await _repository.SaveCampaignAsync(campaign with
        {
            PositionIds = memberIds,
            Revision = expectedRevision + 1,
            CreatedAtUtc = campaign.CreatedAtUtc == default ? now : campaign.CreatedAtUtc,
            UpdatedAtUtc = now,
        }, expectedRevision, cancellationToken);
    }
}

public sealed class ImprovementService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ImprovementService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<ImprovementGoal>> SaveAsync(
        ImprovementGoal goal,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goal.Name) || string.IsNullOrWhiteSpace(goal.Measurement))
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("改进目标需要名称和明确测量口径。");
        }
        if (goal.NotificationEnabled && goal.Rule is null)
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("主观目标不能启用自动桌宠提醒。");
        }
        if (goal.ObservationWindowDays <= 0)
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("观察周期必须至少为 1 天。");
        }
        if (goal.Rule is not null && string.IsNullOrWhiteSpace(goal.RuleVersion))
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("自动目标必须冻结当前测量规则版本。");
        }
        if (goal.BaselineOpportunityCount < 0 || goal.BaselinePassCount < 0 || goal.BaselineFailCount < 0 ||
            goal.BaselinePassCount + goal.BaselineFailCount > goal.BaselineOpportunityCount)
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("目标基线的机会、通过和失败分母不一致。");
        }
        var now = _timeProvider.GetUtcNow();
        var normalized = goal with
        {
            Name = goal.Name.Trim(),
            Measurement = goal.Measurement.Trim(),
            ApplicableSymbols = goal.ApplicableSymbols.Trim(),
            GoalKey = string.IsNullOrWhiteSpace(goal.GoalKey) ? goal.Id : goal.GoalKey,
            Version = Math.Max(1, goal.Version),
            Revision = expectedRevision + 1,
            CreatedAtUtc = goal.CreatedAtUtc == default ? now : goal.CreatedAtUtc,
            UpdatedAtUtc = now,
        };
        if (expectedRevision > 0)
        {
            var data = await _repository.LoadWorkspaceAsync(
                goal.AccountKey, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
            var existing = data.Goals.FirstOrDefault(item => item.Id == goal.Id);
            if (existing is null)
            {
                return ReviewSaveResult<ImprovementGoal>.Missing("改进目标不存在，请刷新后重试。");
            }
            if (!HasSameMeasurementContract(existing, normalized))
            {
                return ReviewSaveResult<ImprovementGoal>.Validation("测量规则、阈值或适用范围改变时必须建立新版本。");
            }
        }
        return await _repository.SaveGoalAsync(normalized, expectedRevision, cancellationToken);
    }

    public async Task<ReviewSaveResult<ImprovementGoal>> CreateVersionAsync(
        ImprovementGoal previousVersion,
        ImprovementGoal nextVersion,
        IReadOnlyCollection<GoalObservation> baselineObservations,
        CancellationToken cancellationToken = default)
    {
        if (previousVersion.Status != ImprovementGoalStatus.Active ||
            previousVersion.AccountKey != nextVersion.AccountKey)
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("只能从当前账户的活动目标建立新版本。");
        }
        var effectiveDate = nextVersion.StartServerDate <= previousVersion.StartServerDate
            ? previousVersion.StartServerDate.AddDays(1)
            : nextVersion.StartServerDate;
        var selected = baselineObservations.Where(item => item.GoalId == previousVersion.Id).ToArray();
        var opportunityCount = selected.Sum(item => item.OpportunityCount);
        var passCount = selected.Sum(item => item.PassCount);
        var failCount = selected.Sum(item => item.FailCount);
        var now = _timeProvider.GetUtcNow();
        var goalKey = string.IsNullOrWhiteSpace(previousVersion.GoalKey)
            ? previousVersion.Id
            : previousVersion.GoalKey;
        var candidate = nextVersion with
        {
            Id = $"{goalKey}:v{Math.Max(1, previousVersion.Version) + 1}",
            GoalKey = goalKey,
            Version = Math.Max(1, previousVersion.Version) + 1,
            StartServerDate = effectiveDate,
            EndServerDate = null,
            BaselineFromServerDate = selected.Length == 0 ? null : selected.Min(item => item.ServerDate),
            BaselineToServerDate = selected.Length == 0 ? null : selected.Max(item => item.ServerDate),
            BaselineOpportunityCount = opportunityCount,
            BaselinePassCount = passCount,
            BaselineFailCount = failCount,
            BaselineValue = passCount + failCount == 0 ? null : passCount * 100m / (passCount + failCount),
            PreviousVersionId = previousVersion.Id,
            Status = ImprovementGoalStatus.Active,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (string.IsNullOrWhiteSpace(candidate.Name) || string.IsNullOrWhiteSpace(candidate.Measurement) ||
            candidate.ObservationWindowDays <= 0 ||
            candidate.NotificationEnabled && candidate.Rule is null ||
            candidate.Rule is not null && string.IsNullOrWhiteSpace(candidate.RuleVersion))
        {
            return ReviewSaveResult<ImprovementGoal>.Validation("新目标版本缺少有效名称、测量规则、窗口或提醒条件。");
        }
        var archived = previousVersion with
        {
            Status = ImprovementGoalStatus.Archived,
            EndServerDate = effectiveDate.AddDays(-1),
            NotificationEnabled = false,
            Revision = previousVersion.Revision + 1,
            UpdatedAtUtc = now,
        };
        return await _repository.SaveGoalVersionAsync(
            archived, previousVersion.Revision, candidate, cancellationToken);
    }

    public Task<ReviewSaveResult<ImprovementGoal>> ArchiveAsync(
        ImprovementGoal goal,
        DateOnly endServerDate,
        CancellationToken cancellationToken = default)
    {
        if (goal.Status == ImprovementGoalStatus.Archived)
        {
            return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Validation("该改进目标已经归档。"));
        }
        if (endServerDate < goal.StartServerDate)
        {
            return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Validation("目标结束日不能早于开始日。"));
        }
        return SaveAsync(goal with
        {
            Status = ImprovementGoalStatus.Archived,
            EndServerDate = endServerDate,
            NotificationEnabled = false,
        }, goal.Revision, cancellationToken);
    }

    private static bool HasSameMeasurementContract(ImprovementGoal left, ImprovementGoal right) =>
        left.Rule == right.Rule &&
        string.Equals(left.RuleVersion, right.RuleVersion, StringComparison.Ordinal) &&
        left.TargetValue == right.TargetValue &&
        string.Equals(left.Measurement, right.Measurement, StringComparison.Ordinal) &&
        string.Equals(left.ApplicableSymbols, right.ApplicableSymbols, StringComparison.OrdinalIgnoreCase) &&
        left.ObservationWindowDays == right.ObservationWindowDays &&
        left.BaselineFromServerDate == right.BaselineFromServerDate &&
        left.BaselineToServerDate == right.BaselineToServerDate &&
        left.BaselineOpportunityCount == right.BaselineOpportunityCount &&
        left.BaselinePassCount == right.BaselinePassCount &&
        left.BaselineFailCount == right.BaselineFailCount;
}

public sealed class BehaviorReviewService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public BehaviorReviewService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task RecordOccurrenceAsync(BehaviorOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        if (occurrence.TradeLinks.Any(item => item.TradeKey.AccountKey != occurrence.AccountKey))
        {
            throw new ArgumentException("行为证据不能关联其他账户的交易。", nameof(occurrence));
        }
        if (occurrence.Source == ReviewEvidenceSource.RuleRecalculation &&
            (occurrence.NotificationDelivered ||
             !string.IsNullOrWhiteSpace(occurrence.NotificationDisposition) &&
             !string.Equals(occurrence.NotificationDisposition, "NotApplicable", StringComparison.Ordinal)))
        {
            throw new ArgumentException("历史规则重算不能产生提醒交付记录。", nameof(occurrence));
        }
        return _repository.SaveBehaviorOccurrenceAsync(occurrence, cancellationToken);
    }

    public Task<ReviewSaveResult<BehaviorOccurrence>> SaveReviewAsync(
        string accountKey,
        string occurrenceId,
        string? userExplanation,
        bool evidenceInsufficient,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey) || string.IsNullOrWhiteSpace(occurrenceId))
        {
            return Task.FromResult(ReviewSaveResult<BehaviorOccurrence>.Validation("请选择需要解释的行为事件。"));
        }
        var explanation = string.IsNullOrWhiteSpace(userExplanation) ? null : userExplanation.Trim();
        if (explanation is null && !evidenceInsufficient)
        {
            return Task.FromResult(ReviewSaveResult<BehaviorOccurrence>.Validation("请填写解释，或标记当前依据不足。"));
        }
        return _repository.SaveBehaviorReviewAsync(
            accountKey, occurrenceId, explanation, evidenceInsufficient, expectedRevision,
            _timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task RecordGoalObservationAsync(GoalObservation observation, CancellationToken cancellationToken = default)
    {
        if (observation.OpportunityCount < 0 || observation.PassCount < 0 || observation.FailCount < 0 ||
            observation.PassCount + observation.FailCount > observation.OpportunityCount)
        {
            throw new ArgumentException("目标观察的机会、通过和失败分母不一致。", nameof(observation));
        }
        var normalized = observation.OpportunityCount == 0
            ? observation with
            {
                PassCount = 0,
                FailCount = 0,
                Status = GoalObservationStatus.NotApplicable,
            }
            : observation;
        return _repository.SaveGoalObservationAsync(normalized, cancellationToken);
    }
}

public sealed class OpportunityService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public OpportunityService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewSaveResult<OpportunityRecord>> SaveAsync(
        OpportunityRecord opportunity,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(opportunity.Symbol) || string.IsNullOrWhiteSpace(opportunity.Reason))
        {
            return ReviewSaveResult<OpportunityRecord>.Validation("机会记录需要精确品种和未参与原因。");
        }
        if (opportunity.Kind == OpportunityRecordKind.ObservedBeforeMove && opportunity.RecordedAtUtc < opportunity.ObservedAtUtc)
        {
            return ReviewSaveResult<OpportunityRecord>.Validation("系统记录时间不能早于观察时间。");
        }
        var data = await _repository.LoadWorkspaceAsync(
            opportunity.AccountKey, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        if (!string.IsNullOrWhiteSpace(opportunity.PlaybookVersionId) &&
            data.Playbooks.All(item => item.Id != opportunity.PlaybookVersionId ||
                                       item.AccountKey != opportunity.AccountKey))
        {
            return ReviewSaveResult<OpportunityRecord>.Validation("关联策略版本不存在或属于其他账户。");
        }
        if (!string.IsNullOrWhiteSpace(opportunity.LinkedTradeKey) &&
            (!long.TryParse(opportunity.LinkedTradeKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var linkedPositionId) ||
             data.Trades.All(item => item.PositionId != linkedPositionId ||
                                     item.AccountKey != opportunity.AccountKey)))
        {
            return ReviewSaveResult<OpportunityRecord>.Validation("关联交易不存在或属于其他账户。");
        }
        var now = _timeProvider.GetUtcNow();
        return await _repository.SaveOpportunityAsync(opportunity with
        {
            Symbol = opportunity.Symbol.Trim(),
            Reason = opportunity.Reason.Trim(),
            Notes = opportunity.Notes.Trim(),
            Conditions = opportunity.Conditions.Trim(),
            RecordedAtUtc = now,
            Revision = expectedRevision + 1,
        }, expectedRevision, cancellationToken);
    }
}

public sealed class ReviewFilterService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ReviewFilterService(IReviewWorkspaceRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ReviewSaveResult<ReviewSavedFilter>> SaveAsync(
        ReviewSavedFilter filter,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filter.Name))
        {
            return Task.FromResult(ReviewSaveResult<ReviewSavedFilter>.Validation("保存筛选需要名称。"));
        }
        try
        {
            using var _ = JsonDocument.Parse(filter.FilterJson);
        }
        catch (JsonException)
        {
            return Task.FromResult(ReviewSaveResult<ReviewSavedFilter>.Validation("筛选内容不是有效 JSON。"));
        }
        var now = _timeProvider.GetUtcNow();
        return _repository.SaveFilterAsync(filter with
        {
            Name = filter.Name.Trim(),
            Revision = expectedRevision + 1,
            CreatedAtUtc = filter.CreatedAtUtc == default ? now : filter.CreatedAtUtc,
            UpdatedAtUtc = now,
        }, expectedRevision, cancellationToken);
    }
}

public sealed class TradingSessionService
{
    private readonly IReviewWorkspaceRepository _repository;

    public TradingSessionService(IReviewWorkspaceRepository repository) =>
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<ReviewSaveResult<TradingSessionDefinition>> SaveAsync(
        TradingSessionDefinition session,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.AccountKey) || string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(session.Name) || string.IsNullOrWhiteSpace(session.TimeZoneId))
        {
            return ReviewSaveResult<TradingSessionDefinition>.Validation("交易时段需要账户、名称和具名时区。");
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return ReviewSaveResult<TradingSessionDefinition>.Validation("无法识别该具名时区。");
        }
        var data = await _repository.LoadWorkspaceAsync(
            session.AccountKey, DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        if ((data.TradingSessions ?? []).Any(item => item.Id != session.Id &&
            item.Name.Equals(session.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return ReviewSaveResult<TradingSessionDefinition>.Validation("同一账户不能保存重名交易时段。");
        }
        var normalized = session with
        {
            Name = session.Name.Trim(),
            TimeZoneId = session.TimeZoneId.Trim(),
            StartDays = (session.StartDays ?? []).Distinct().Order().ToArray(),
        };
        return await _repository.SaveTradingSessionAsync(normalized, expectedRevision, cancellationToken);
    }
}

public sealed class ReviewCacheService
{
    private readonly IReviewWorkspaceRepository _repository;

    public ReviewCacheService(IReviewWorkspaceRepository repository) =>
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public Task<ReviewCacheCleanupResult> ClearMarketDataAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        return _repository.ClearMarketDataCacheAsync(accountKey, cancellationToken);
    }
}

public sealed class TradeReplayService
{
    private readonly IReviewWorkspaceRepository _repository;
    private readonly IMarketHistorySource _historySource;
    private readonly ReviewWorkspaceCalculator _calculator = new();

    public TradeReplayService(IReviewWorkspaceRepository repository, IMarketHistorySource historySource)
    {
        _repository = repository;
        _historySource = historySource;
    }

    public async Task<MarketHistoryResult> LoadAsync(MarketHistoryRequest request, CancellationToken cancellationToken = default)
    {
        var cached = await _repository.LoadMarketDataAsync(request.ExpectedAccountKey, request.TerminalId,
            request.Symbol, request.Timeframe, request.Precision, request.FromUtc, request.ToUtc, cancellationToken);
        if (cached.Range is { Coverage: MarketCoverageStatus.Complete })
        {
            return new MarketHistoryResult(cached.Range, cached.Bars, cached.Ticks);
        }
        var result = await _historySource.LoadAsync(request, cancellationToken);
        if (result.Range.AccountKey != request.ExpectedAccountKey || result.Range.RequestId != request.RequestId)
        {
            throw new InvalidDataException("历史行情响应账户或请求身份不匹配。");
        }
        await _repository.SaveMarketDataAsync(result.Range, result.Bars, result.Ticks, cancellationToken);
        return result;
    }

    public ReplayFrame CreateFrame(
        DateTimeOffset cursorUtc,
        MarketHistoryResult history,
        TradeDetailData detail,
        IReadOnlyCollection<ReviewEvidenceStamp> notes,
        bool revealFullReview = false) =>
        _calculator.BuildReplayFrame(cursorUtc, history.Range, history.Bars, history.Ticks,
            detail.Deals, detail.PnlSamples, detail.Behaviors, notes,
            detail.Trade, detail.Plan, detail.Document, revealFullReview);
}

public sealed class ReviewExportService
{
    private static readonly Regex WindowsPathPattern = new(
        @"(?i)(?:[a-z]:[\\/]|\\\\)[^\s<>""']+", RegexOptions.Compiled);
    private readonly TimeProvider _timeProvider;

    public ReviewExportService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public ReviewExportPackage Build(
        ReviewWorkspaceSnapshot snapshot,
        IReadOnlyCollection<TradeDetailSnapshot> details,
        ReviewWorkspaceData? workspaceData = null,
        ReviewExportMode mode = ReviewExportMode.PublicShare,
        ReviewExportScope scope = ReviewExportScope.AllFiltered,
        IReadOnlyList<ReviewAttachment>? includedAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var accountLabel = mode == ReviewExportMode.PublicShare ? "已隐藏" : snapshot.Filter.AccountKey;
        var currency = mode == ReviewExportMode.PublicShare
            ? SanitizePublicText(workspaceData?.Currency ?? string.Empty, snapshot.Filter.AccountKey)
            : workspaceData?.Currency ?? string.Empty;
        var exportedTrades = snapshot.AllFilteredTrades ?? snapshot.Trades;
        var detailByPosition = details.ToDictionary(detail => detail.Trade.PositionId);
        if (exportedTrades.Any(trade => trade.AccountKey != snapshot.Filter.AccountKey ||
                                        !detailByPosition.TryGetValue(trade.PositionId, out var detail) ||
                                        detail.Trade.AccountKey != trade.AccountKey ||
                                        detail.SourceVersion != snapshot.Version.Token) ||
            detailByPosition.Count != exportedTrades.Count)
        {
            throw new InvalidDataException("导出交易详情、账户或数据版本与冻结范围不一致。");
        }
        if (mode == ReviewExportMode.PublicShare && includedAttachments is { Count: > 0 })
        {
            var opportunityIds = scope == ReviewExportScope.AllFiltered
                ? workspaceData?.Opportunities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? []
                : new HashSet<string>(StringComparer.Ordinal);
            var allowedLinks = details.SelectMany(detail => detail.Attachments)
                .Concat(scope == ReviewExportScope.AllFiltered
                    ? (workspaceData?.OpportunityAttachments ?? []).Where(item =>
                        item.OwnerKind == "opportunity" && opportunityIds.Contains(item.OwnerId))
                    : [])
                .Select(item => (item.Id, item.AccountKey, item.OwnerKind, item.OwnerId))
                .ToHashSet();
            if (includedAttachments.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
                    includedAttachments.Count ||
                includedAttachments.Any(item => item.AccountKey != snapshot.Filter.AccountKey ||
                    !allowedLinks.Contains((item.Id, item.AccountKey, item.OwnerKind, item.OwnerId))))
            {
                throw new InvalidDataException("所选附件不属于当前冻结导出范围。");
            }
        }
        var netPnl = exportedTrades.Sum(trade => trade.NetPnl);
        var revisionTotal = details.Sum(detail => detail.Document?.Revision ?? 0);
        var reviewedCount = details.Count(detail => detail.Document?.Status == ReviewCompletionStatus.Reviewed);
        var reviewCompletionPercentage = exportedTrades.Count == 0
            ? 0m : 100m * reviewedCount / exportedTrades.Count;
        var publicIds = exportedTrades.Select((trade, index) => (trade.PositionId, Id: $"T{index + 1:D6}"))
            .ToDictionary(item => item.PositionId, item => item.Id);
        var detailDocuments = details
            .Where(detail => detail.Document is not null)
            .ToDictionary(detail => detail.Trade.PositionId, detail => detail.Document!);
        var csv = new StringBuilder("account_key,position_id,symbol,side,opened_at_utc,closed_at_utc,net_pnl,review_status,review_revision,summary\r\n");
        foreach (var trade in exportedTrades)
        {
            if (!detailDocuments.TryGetValue(trade.PositionId, out var document))
            {
                snapshot.Documents.TryGetValue(trade.PositionId, out document);
            }
            csv.Append(Csv(accountLabel)).Append(',')
                .Append(mode == ReviewExportMode.PublicShare
                    ? Csv(publicIds[trade.PositionId]) : CsvIdentifier(trade.PositionId)).Append(',')
                .Append(Csv(ExportText(trade.Symbol))).Append(',').Append(Csv(trade.Side.ToString())).Append(',')
                .Append(Csv(trade.OpenedAtUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(trade.ClosedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
                .Append(Csv(trade.NetPnl.ToString(CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv((document?.Status ?? ReviewCompletionStatus.Pending).ToString())).Append(',')
                .Append(Csv((document?.Revision ?? 0).ToString(CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(ExportText(document?.Summary ?? string.Empty))).Append("\r\n");
        }
        var publicFingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(snapshot.Version.Token))).ToLowerInvariant();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = mode == ReviewExportMode.PublicShare ? 2 : 1,
            generatedAtUtc = _timeProvider.GetUtcNow(),
            exportMode = mode.ToString(),
            exportScope = scope.ToString(),
            account = accountLabel,
            currency,
            snapshotToken = mode == ReviewExportMode.PublicShare ? publicFingerprint : snapshot.Version.Token,
            timeZoneOffsetSeconds = snapshot.Filter.ServerUtcOffsetSeconds,
            range = new
            {
                fromServerDate = snapshot.Filter.FromServerDate,
                toServerDate = snapshot.Filter.ToServerDate,
            },
            tradeCount = exportedTrades.Count,
            netPnl,
            revisionTotal,
            opportunityCount = scope == ReviewExportScope.AllFiltered ? workspaceData?.Opportunities.Count ?? 0 : 0,
            includedAttachmentCount = includedAttachments?.Count ?? 0,
        });
        var data = mode == ReviewExportMode.PublicShare
            ? BuildPublicData(snapshot, details, workspaceData, scope, includedAttachments ?? [], publicIds, netPnl)
            : BuildData(snapshot, details, workspaceData, mode);
        var markdown = BuildMarkdown(snapshot, workspaceData, accountLabel, currency, exportedTrades.Count,
            netPnl, revisionTotal, reviewCompletionPercentage, scope);
        var html = BuildHtml(snapshot, workspaceData, accountLabel, currency, exportedTrades, detailDocuments,
            publicIds, mode, netPnl, revisionTotal, scope);
        return new ReviewExportPackage(
            $"TradePet-review-{snapshot.Filter.FromServerDate:yyyyMMdd}-{snapshot.Filter.ToServerDate:yyyyMMdd}-{_timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.zip",
            snapshot.Version.Token,
            [
                new ReviewExportEntry("trades.csv", "text/csv", new UTF8Encoding(true).GetBytes(csv.ToString())),
                new ReviewExportEntry("review.md", "text/markdown", Encoding.UTF8.GetBytes(markdown)),
                new ReviewExportEntry("review.html", "text/html", Encoding.UTF8.GetBytes(html)),
                new ReviewExportEntry("review-data.json", "application/json", data),
                new ReviewExportEntry("manifest.json", "application/json", manifest),
            ]);

        string ExportText(string value) => mode == ReviewExportMode.PublicShare
            ? SanitizePublicText(value, snapshot.Filter.AccountKey) : value;
    }

    public static string PublicAttachmentPath(int ordinal, ReviewAttachment attachment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or
                              ".pdf" or ".txt" or ".md" or ".csv"))
        {
            extension = ".bin";
        }
        return $"attachments/A{ordinal:D6}/asset{extension}";
    }

    public static string PreviewPublicText(string value, string accountKey) =>
        SanitizePublicText(value, accountKey);

    private static byte[] BuildPublicData(
        ReviewWorkspaceSnapshot snapshot,
        IReadOnlyCollection<TradeDetailSnapshot> details,
        ReviewWorkspaceData? workspaceData,
        ReviewExportScope scope,
        IReadOnlyList<ReviewAttachment> includedAttachments,
        IReadOnlyDictionary<long, string> publicIds,
        decimal netPnl)
    {
        var accountKey = snapshot.Filter.AccountKey;
        var exportedTrades = snapshot.AllFilteredTrades ?? snapshot.Trades;
        var detailByPosition = details.ToDictionary(detail => detail.Trade.PositionId);
        var plans = details.Where(detail => detail.Plan is not null)
            .Select(detail => detail.Plan!)
            .DistinctBy(plan => plan.Id)
            .ToArray();
        var planIds = plans.Select((plan, index) => (plan.Id, PublicId: $"P{index + 1:D6}"))
            .ToDictionary(item => item.Id, item => item.PublicId, StringComparer.Ordinal);
        var behaviors = snapshot.BehaviorOccurrences
            .Where(item => item.AccountKey == accountKey &&
                           item.TradeLinks.Any(link => link.TradeKey.AccountKey == accountKey &&
                                                       publicIds.ContainsKey(link.TradeKey.PositionId)))
            .ToArray();
        var opportunities = scope == ReviewExportScope.AllFiltered
            ? workspaceData?.Opportunities ?? []
            : [];
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            exportScope = scope.ToString(),
            range = new { snapshot.Filter.FromServerDate, snapshot.Filter.ToServerDate },
            currency = SanitizePublicText(workspaceData?.Currency ?? string.Empty, accountKey),
            tradeCount = exportedTrades.Count,
            netPnl,
            revisionTotal = details.Sum(detail => detail.Document?.Revision ?? 0),
            trades = exportedTrades.Select(trade =>
            {
                var detail = detailByPosition[trade.PositionId];
                return new
                {
                    id = publicIds[trade.PositionId],
                    symbol = SanitizePublicText(trade.Symbol, accountKey),
                    side = trade.Side.ToString(),
                    trade.OpenedAtUtc,
                    trade.ClosedAtUtc,
                    trade.NetPnl,
                    planId = detail.Plan is null ? null : planIds[detail.Plan.Id],
                    review = detail.Document is null ? null : new
                    {
                        status = detail.Document.Status.ToString(),
                        detail.Document.Revision,
                        summary = SanitizePublicText(detail.Document.Summary, accountKey),
                        nextAction = SanitizePublicText(detail.Document.NextAction, accountKey),
                    },
                };
            }),
            plans = plans.Select(plan => new
            {
                id = planIds[plan.Id],
                symbol = SanitizePublicText(plan.Symbol, accountKey),
                strategy = SanitizePublicText(plan.Strategy, accountKey),
                setup = SanitizePublicText(plan.Setup, accountKey),
                notes = SanitizePublicText(plan.Notes, accountKey),
            }),
            behaviors = behaviors.Select((behavior, index) => new
            {
                id = $"B{index + 1:D6}",
                rule = behavior.Rule.ToString(),
                behavior.EventAtUtc,
                behavior.Value,
                behavior.Baseline,
                behavior.Threshold,
                summary = SanitizePublicText(behavior.Summary, accountKey),
                tradeIds = behavior.TradeLinks
                    .Where(link => link.TradeKey.AccountKey == accountKey &&
                                   publicIds.ContainsKey(link.TradeKey.PositionId))
                    .Select(link => publicIds[link.TradeKey.PositionId])
                    .Distinct(),
            }),
            opportunities = opportunities.Select((opportunity, index) => new
            {
                id = $"O{index + 1:D6}",
                kind = opportunity.Kind.ToString(),
                opportunity.ServerDate,
                symbol = SanitizePublicText(opportunity.Symbol, accountKey),
                reason = SanitizePublicText(opportunity.Reason, accountKey),
                conditions = SanitizePublicText(opportunity.Conditions, accountKey),
                notes = SanitizePublicText(opportunity.Notes, accountKey),
                linkedTradeId = long.TryParse(opportunity.LinkedTradeKey,
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var linkedPosition) &&
                                publicIds.TryGetValue(linkedPosition, out var publicId)
                    ? publicId : null,
            }),
            dailyJournals = scope == ReviewExportScope.AllFiltered
                ? workspaceData?.DailyJournals.Values.OrderBy(item => item.ServerDate).Select(journal => new
                {
                    journal.ServerDate,
                    summary = SanitizePublicText(journal.PostMarketSummary, accountKey),
                    nextAction = SanitizePublicText(journal.NextAction, accountKey),
                    status = journal.Status.ToString(),
                    journal.Revision,
                })
                : null,
            attachments = includedAttachments.Select((attachment, index) => new
            {
                id = $"A{index + 1:D6}",
                exportPath = PublicAttachmentPath(index + 1, attachment),
                attachment.MediaType,
                attachment.SizeBytes,
                attachment.Sha256,
                title = SanitizePublicText(attachment.Title, accountKey),
            }),
        });
    }

    private static string SanitizePublicText(string value, string accountKey)
    {
        var result = WindowsPathPattern.Replace(value, "[本机路径已隐藏]")
            .Replace(accountKey, "[账户已隐藏]", StringComparison.OrdinalIgnoreCase);
        var parts = accountKey.Split('|', 2);
        if (parts[0].Length >= 4)
        {
            result = result.Replace(parts[0], "[服务器已隐藏]", StringComparison.OrdinalIgnoreCase);
        }
        if (parts.Length == 2 && parts[1].Length >= 5)
        {
            result = Regex.Replace(result, $@"(?<!\d){Regex.Escape(parts[1])}(?!\d)", "[账号已隐藏]");
        }
        return result;
    }

    private static byte[] BuildData(
        ReviewWorkspaceSnapshot snapshot,
        IReadOnlyCollection<TradeDetailSnapshot> details,
        ReviewWorkspaceData? workspaceData,
        ReviewExportMode mode) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            account = mode == ReviewExportMode.PublicShare ? null : snapshot.Filter.AccountKey,
            trades = details.Select(detail => new
            {
                positionId = detail.Trade.PositionId.ToString(CultureInfo.InvariantCulture),
                detail.Trade.Symbol,
                side = detail.Trade.Side.ToString(),
                detail.Trade.OpenedAtUtc,
                detail.Trade.ClosedAtUtc,
                detail.Trade.OpenServerDate,
                detail.Trade.CloseServerDate,
                detail.Trade.EntryPrice,
                detail.Trade.ExitPrice,
                detail.Trade.OpeningVolume,
                detail.Trade.MaximumVolume,
                detail.Trade.NetPnl,
                deals = detail.Deals.Select(deal => new
                {
                    ticket = deal.Ticket.ToString(CultureInfo.InvariantCulture),
                    orderTicket = deal.OrderTicket.ToString(CultureInfo.InvariantCulture),
                    positionId = deal.PositionId.ToString(CultureInfo.InvariantCulture),
                    deal.Symbol,
                    side = deal.Side.ToString(),
                    entryKind = deal.EntryKind.ToString(),
                    deal.Volume,
                    deal.Price,
                    deal.Profit,
                    deal.Commission,
                    deal.Swap,
                    deal.Fee,
                    deal.OccurredAtUtc,
                }),
                metadata = detail.Metadata is null ? null : new
                {
                    detail.Metadata.PlanId,
                    complianceStatus = detail.Metadata.ComplianceStatus.ToString(),
                    detail.Metadata.Strategy,
                    detail.Metadata.Setup,
                    detail.Metadata.Tags,
                    detail.Metadata.UserEdited,
                    detail.Metadata.UpdatedAtUtc,
                },
                review = detail.Document is null ? null : new
                {
                    status = detail.Document.Status.ToString(),
                    detail.Document.EntryReason,
                    detail.Document.ExitReason,
                    detail.Document.DidWell,
                    detail.Document.ToImprove,
                    detail.Document.NextAction,
                    detail.Document.Summary,
                    detail.Document.Emotion,
                    detail.Document.MarketCondition,
                    detail.Document.Revision,
                    detail.Document.SourceVersion,
                    detail.Document.RuleVersion,
                    detail.Document.ReviewedAtUtc,
                },
                assessments = detail.Assessments.Select(value => new
                {
                    value.PlaybookVersionId,
                    value.RuleId,
                    status = value.Status.ToString(),
                    source = value.Source.ToString(),
                    value.EvidenceReference,
                    value.Notes,
                    value.Revision,
                    value.UpdatedAtUtc,
                }),
                behaviors = detail.Behaviors.Select(value => new
                {
                    value.Id,
                    rule = value.Rule.ToString(),
                    value.RuleVersion,
                    value.EventAtUtc,
                    value.ObservedAtUtc,
                    value.Summary,
                    value.Value,
                    value.Baseline,
                    value.Threshold,
                    value.MissingData,
                    value.NotificationDelivered,
                    value.NotificationDisposition,
                    value.UserExplanation,
                    tradeLinks = value.TradeLinks.Select(link => new
                    {
                        positionId = link.TradeKey.PositionId.ToString(CultureInfo.InvariantCulture),
                        role = link.Role.ToString(),
                    }),
                }),
                attachments = detail.Attachments.Select(value => new
                {
                    value.Id,
                    value.Sha256,
                    value.FileName,
                    value.MediaType,
                    value.SizeBytes,
                    value.Title,
                    value.EventReference,
                    exportPath = $"attachments/{value.Id}/{Path.GetFileName(value.FileName)}",
                }),
            }),
            dailyJournals = workspaceData?.DailyJournals.Values.OrderBy(value => value.ServerDate).Select(value => new
            {
                value.ServerDate,
                value.PreMarketPlan,
                value.IntradayNotes,
                value.PostMarketSummary,
                value.DidWell,
                value.ToImprove,
                value.NextAction,
                status = value.Status.ToString(),
                value.PreMarketRecordedAtUtc,
                value.IntradayRecordedAtUtc,
                value.PostMarketRecordedAtUtc,
            }),
            periodReviews = workspaceData?.PeriodReviews?.Select(value => new
            {
                value.Id,
                value.FromServerDate,
                value.ToServerDate,
                value.Facts,
                value.DidWell,
                value.ToImprove,
                value.NextAction,
                linkedPositionIds = value.LinkedTrades.Select(key => key.PositionId.ToString(CultureInfo.InvariantCulture)),
            }),
            opportunities = workspaceData?.Opportunities.Select(value => new
            {
                value.Id,
                kind = value.Kind.ToString(),
                value.ObservedAtUtc,
                value.RecordedAtUtc,
                value.ServerDate,
                value.Symbol,
                side = value.Side?.ToString(),
                value.PlaybookVersionId,
                value.EntryPrice,
                value.StopPrice,
                value.TargetPrice,
                value.Reason,
                value.Conditions,
                value.Notes,
                linkedTrade = mode == ReviewExportMode.PublicShare ? null : value.LinkedTradeKey,
            }),
            opportunityAttachments = workspaceData?.OpportunityAttachments?.Select(value => new
            {
                value.Id,
                opportunityId = value.OwnerId,
                value.Sha256,
                value.FileName,
                value.MediaType,
                value.SizeBytes,
                value.Title,
                value.EventReference,
                exportPath = $"attachments/{value.Id}/{Path.GetFileName(value.FileName)}",
            }),
        });

    private static string BuildMarkdown(
        ReviewWorkspaceSnapshot snapshot,
        ReviewWorkspaceData? workspaceData,
        string accountLabel,
        string currency,
        int tradeCount,
        decimal netPnl,
        int revisionTotal,
        decimal reviewCompletionPercentage,
        ReviewExportScope scope) => $"""
        # TradePet 复盘导出

        - 账户：{accountLabel}
        - 账户币种：{(string.IsNullOrWhiteSpace(currency) ? "未知" : currency)}
        - 范围：{snapshot.Filter.FromServerDate:yyyy-MM-dd} 至 {snapshot.Filter.ToServerDate:yyyy-MM-dd}
        - 服务器时区偏移：UTC{FormatOffset(snapshot.Filter.ServerUtcOffsetSeconds)}
        - 完整交易：{tradeCount}
        - 净盈亏：{netPnl.ToString(CultureInfo.InvariantCulture)}
        - 复盘修订合计：{revisionTotal}
        - 复盘完成率：{reviewCompletionPercentage:0.##}%
        - 行情覆盖率：{snapshot.DataQuality.MarketDataCoveragePercentage:0.##}%
        此导出按完整交易最终平仓日统计；日历现金账按成交发生日统计。

        ## 日记与周期总结

        - 日记数量：{(scope == ReviewExportScope.AllFiltered ? workspaceData?.DailyJournals.Count ?? 0 : 0)}
        - 周期总结数量：{(scope == ReviewExportScope.AllFiltered ? workspaceData?.PeriodReviews?.Count ?? 0 : 0)}
        """;

    private static string BuildHtml(
        ReviewWorkspaceSnapshot snapshot,
        ReviewWorkspaceData? workspaceData,
        string accountLabel,
        string currency,
        IReadOnlyList<TradeRecord> exportedTrades,
        IReadOnlyDictionary<long, TradeReviewDocument> detailDocuments,
        IReadOnlyDictionary<long, string> publicIds,
        ReviewExportMode mode,
        decimal netPnl,
        int revisionTotal,
        ReviewExportScope scope)
    {
        var body = new StringBuilder();
        foreach (var trade in exportedTrades)
        {
            if (!detailDocuments.TryGetValue(trade.PositionId, out var document))
            {
                snapshot.Documents.TryGetValue(trade.PositionId, out document);
            }
            var tradeId = mode == ReviewExportMode.PublicShare
                ? publicIds[trade.PositionId] : trade.PositionId.ToString(CultureInfo.InvariantCulture);
            var symbol = mode == ReviewExportMode.PublicShare
                ? SanitizePublicText(trade.Symbol, snapshot.Filter.AccountKey) : trade.Symbol;
            var summary = mode == ReviewExportMode.PublicShare
                ? SanitizePublicText(document?.Summary ?? string.Empty, snapshot.Filter.AccountKey)
                : document?.Summary ?? string.Empty;
            body.Append("<tr><td>").Append(Html(tradeId))
                .Append("</td><td>").Append(Html(symbol))
                .Append("</td><td>").Append(Html(trade.Side.ToString()))
                .Append("</td><td>").Append(Html(trade.NetPnl.ToString(CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(document?.Revision ?? 0)
                .Append("</td><td>").Append(Html(summary)).Append("</td></tr>");
        }
        var journals = new StringBuilder();
        foreach (var journal in scope != ReviewExportScope.AllFiltered || workspaceData is null
                     ? Enumerable.Empty<DailyJournal>()
                     : workspaceData.DailyJournals.Values.OrderBy(value => value.ServerDate))
        {
            var summary = mode == ReviewExportMode.PublicShare
                ? SanitizePublicText(journal.PostMarketSummary, snapshot.Filter.AccountKey)
                : journal.PostMarketSummary;
            journals.Append("<article><h3>").Append(Html(journal.ServerDate.ToString("yyyy-MM-dd")))
                .Append("</h3><p>").Append(Html(summary)).Append("</p></article>");
        }
        return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>TradePet 复盘导出</title>
            <style>body{font-family:system-ui,sans-serif;max-width:1100px;margin:32px auto;padding:0 20px;color:#18202a}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccd3dc;padding:8px;text-align:left;vertical-align:top}th{background:#f3f6f9}</style></head>
            <body><h1>TradePet 复盘导出</h1><ul>
            <li>账户：{{Html(accountLabel)}}</li><li>账户币种：{{Html(string.IsNullOrWhiteSpace(currency) ? "未知" : currency)}}</li>
            <li>范围：{{snapshot.Filter.FromServerDate:yyyy-MM-dd}} 至 {{snapshot.Filter.ToServerDate:yyyy-MM-dd}}</li>
            <li>服务器时区偏移：UTC{{Html(FormatOffset(snapshot.Filter.ServerUtcOffsetSeconds))}}</li>
            <li>完整交易：{{exportedTrades.Count}}</li><li>净盈亏：{{netPnl.ToString(CultureInfo.InvariantCulture)}}</li>
            <li>复盘修订合计：{{revisionTotal}}</li></ul>
            <p>完整交易按最终平仓服务器日统计；日历现金账按成交发生日统计。</p>
            <h2>交易</h2><table><thead><tr><th>交易 ID</th><th>品种</th><th>方向</th><th>净盈亏</th><th>复盘修订</th><th>复盘结论</th></tr></thead><tbody>{{body}}</tbody></table>
            <h2>日记</h2>{{journals}}</body></html>
            """;
    }

    private static string Csv(string value)
    {
        var safe = value;
        var probe = safe.AsSpan();
        while (!probe.IsEmpty && (char.IsWhiteSpace(probe[0]) || probe[0] == '\uFEFF'))
        {
            probe = probe[1..];
        }
        if (!probe.IsEmpty && probe[0] is '=' or '+' or '-' or '@')
        {
            safe = "'" + safe;
        }
        return '"' + safe.Replace("\"", "\"\"") + '"';
    }

    private static string CsvIdentifier(long value) =>
        Csv("'" + value.ToString(CultureInfo.InvariantCulture));

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string FormatOffset(int seconds)
    {
        var offset = TimeSpan.FromSeconds(seconds);
        return $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
    }
}
