using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Review;
using Xunit;

namespace TradePet.Application.Tests;

public sealed class ReviewServicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Journal_RequiresPersistedDraftAssessmentAndCompleteTradeBeforeReviewed()
    {
        var repository = FakeRepository.Create();
        var service = new JournalService(repository, new FixedTimeProvider(Now));
        var key = new TradeKey("Broker|1", 1);
        var command = new SaveTradeReviewCommand(key, "顺势", "到目标", "等待确认", "", "继续等确认", "按计划正常结束",
            "平静", "趋势", "source-1", "rule-1", ReviewCompletionStatus.Reviewed);

        var draft = await service.SaveTradeReviewAsync(command, 0);
        var rejected = await service.MarkReviewedAsync(key, "source-1", "rule-1", 1);
        repository.Assessments.Add(new TradeRuleAssessment(key, "p1", "r1", RuleAssessmentStatus.Passed,
            ReviewEvidenceSource.UserBackfill, "note", "", 1, Now));
        var reviewed = await service.MarkReviewedAsync(key, "source-1", "rule-1", 1);

        Assert.True(draft.IsSaved);
        Assert.Equal(ReviewCompletionStatus.Draft, draft.Value!.Status);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, rejected.Status);
        Assert.True(reviewed.IsSaved);
        Assert.Equal(ReviewCompletionStatus.Reviewed, reviewed.Value!.Status);
    }

    [Fact]
    public async Task Journal_RejectsMissingIncompleteAndMismatchedTradeSources()
    {
        var missingRepository = FakeRepository.Create();
        missingRepository.Trades.Clear();
        var command = new SaveTradeReviewCommand(
            new TradeKey("Broker|1", 1), "", "", "", "", "next", "summary", "", "",
            "source-1", "rule-1", ReviewCompletionStatus.Draft);

        var missing = await new JournalService(missingRepository).SaveTradeReviewAsync(command, 0);

        Assert.Equal(ReviewSaveStatus.NotFound, missing.Status);
        Assert.Null(missingRepository.Document);

        var incompleteRepository = FakeRepository.Create();
        incompleteRepository.Trades[0] = incompleteRepository.Trades[0] with
        {
            ClosedAtUtc = null,
            CloseServerDate = null,
            IsComplete = false,
        };
        var incomplete = await new JournalService(incompleteRepository).SaveTradeReviewAsync(command, 0);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, incomplete.Status);

        var mismatchedRepository = FakeRepository.Create();
        mismatchedRepository.TradeDetailOverride = CreateDetail(
            Trade(1, 10m) with { AccountKey = "Broker|other" });
        var mismatched = await new JournalService(mismatchedRepository).SaveTradeReviewAsync(command, 0);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, mismatched.Status);
    }

    [Fact]
    public async Task Journal_EditSubmissionRejectsIdentityTargetMismatchBeforeWriting()
    {
        var repository = FakeRepository.Create();
        var command = new SaveTradeReviewCommand(
            new TradeKey("Broker|1", 1), "", "", "", "", "next", "summary", "", "",
            "source-1", "rule-1", ReviewCompletionStatus.Draft);
        var identity = EditIdentity.ForTrade(new TradeKey("Broker|other", 1), 3, Guid.NewGuid());
        var submission = new TradeReviewEditSubmission(
            new EditSnapshot<SaveTradeReviewCommand>(identity, 1, command), 0);

        var receipt = await new JournalService(repository).SaveTradeReviewAsync(submission);

        Assert.Equal(EditSaveStatus.ValidationFailed, receipt.Status);
        Assert.Null(repository.Document);
    }

    [Fact]
    public async Task Journal_SourceChangeMarksReviewedDocumentForReviewWithoutLosingConclusion()
    {
        var repository = FakeRepository.Create();
        repository.Document = Document(ReviewCompletionStatus.Reviewed, 1, "旧结论") with
        {
            ReviewedSourceVersion = "source-1", ReviewedRuleVersion = "rule-1",
        };
        var service = new JournalService(repository, new FixedTimeProvider(Now));

        var result = await service.MarkNeedsReviewIfChangedAsync(new TradeKey("Broker|1", 1), "source-2", "rule-1");

        Assert.True(result!.IsSaved);
        Assert.Equal(ReviewCompletionStatus.NeedsReview, result.Value!.Status);
        Assert.Equal("旧结论", result.Value.Summary);
        Assert.Equal("source-1", result.Value.ReviewedSourceVersion);
    }

    [Fact]
    public async Task DailyJournal_CompletesAgainstCurrentSourceAndLaterMarksNeedsReviewWithoutOverwritingText()
    {
        var repository = FakeRepository.Create();
        var service = new JournalService(repository, new FixedTimeProvider(Now));
        var date = new DateOnly(2026, 9, 6);
        var journal = new DailyJournal("Broker|1", date, "只做顺势", "按计划等待", "执行事实",
            "等确认", "少追单", "下一次检查触发", ReviewCompletionStatus.Draft, 0, "0", default, default);

        var completed = await service.CompleteDailyJournalAsync(journal, 0, "daily-1");
        var stale = await service.MarkDailyNeedsReviewIfChangedAsync(completed.Value!, "daily-2");

        Assert.True(completed.IsSaved);
        Assert.Equal(ReviewCompletionStatus.Reviewed, completed.Value!.Status);
        Assert.Equal("daily-1", completed.Value.ReviewedSourceVersion);
        Assert.Equal(Now, completed.Value.ReviewedAtUtc);
        Assert.True(stale!.IsSaved);
        Assert.Equal(ReviewCompletionStatus.NeedsReview, stale.Value!.Status);
        Assert.Equal("执行事实", stale.Value.PostMarketSummary);
        Assert.Equal("下一次检查触发", stale.Value.NextAction);
        Assert.Equal("daily-1", stale.Value.ReviewedSourceVersion);
        Assert.Equal("daily-2", stale.Value.SourceVersion);
    }

    [Fact]
    public async Task Query_DetectsStaleSessionAndPaginatesWithoutChangingAnalyticsSample()
    {
        var repository = FakeRepository.Create();
        repository.Trades.Add(Trade(2, -5m));
        var service = new ReviewQueryService(repository);
        long generation = 8;
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30), PageSize: 1);

        var result = await service.QueryAsync(new ReviewQueryContext("q", 7, "Broker|1"), filter, () => generation);

        Assert.False(result.IsCurrentSession);
        Assert.Single(result.Snapshot.Trades);
        Assert.Equal(2, result.Snapshot.TotalCount);
        Assert.Equal(2, result.Snapshot.AllFilteredTrades!.Count);
        Assert.Equal(5m, result.Snapshot.Analytics.Performance.NetPnl);
    }

    [Fact]
    public async Task Query_SeparatesHundredItemPagesFromAll251FilteredTradesAndCachesEachFilter()
    {
        var repository = FakeRepository.Create();
        for (var positionId = 2; positionId <= 251; positionId++)
        {
            repository.Trades.Add(Trade(positionId, positionId));
        }
        var service = new ReviewQueryService(repository);
        var firstFilter = new ReviewWorkspaceFilter(
            "Broker|1", new(2026, 9, 1), new(2026, 9, 30), Page: 1, PageSize: 100);
        var lastFilter = firstFilter with { Page = 3 };

        var first = await service.QueryAsync(new ReviewQueryContext("page-1", 1, "Broker|1"), firstFilter, () => 1);
        var last = await service.QueryAsync(new ReviewQueryContext("page-3", 1, "Broker|1"), lastFilter, () => 1);
        var repeated = await service.QueryAsync(new ReviewQueryContext("page-3-repeat", 1, "Broker|1"), lastFilter, () => 1);

        Assert.Equal(100, first.Snapshot.Trades.Count);
        Assert.Equal(51, last.Snapshot.Trades.Count);
        Assert.Equal(251, first.Snapshot.TotalCount);
        Assert.Equal(251, first.Snapshot.AllFilteredTrades!.Count);
        Assert.Equal(251, last.Snapshot.AllFilteredTrades!.Count);
        Assert.Equal(251, first.Snapshot.AllFilteredTrades.Select(item => item.PositionId).Distinct().Count());
        Assert.NotSame(first.Snapshot, last.Snapshot);
        Assert.Same(last.Snapshot, repeated.Snapshot);
        Assert.Equal(1, repository.WorkspaceLoadCount);
    }

    [Fact]
    public async Task Query_ForwardsCancellationToWorkspaceRepository()
    {
        var repository = FakeRepository.Create();
        repository.WorkspaceLoadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReviewQueryService(repository);
        using var cancellation = new CancellationTokenSource();
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30));

        var query = service.QueryAsync(new ReviewQueryContext("cancel", 1, "Broker|1"), filter, () => 1, cancellation.Token);
        await repository.WorkspaceLoadStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
    }

    [Fact]
    public async Task Query_ReusesSameVersionSnapshotAndReloadsAfterVersionChange()
    {
        var repository = FakeRepository.Create();
        var service = new ReviewQueryService(repository);
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30));

        var first = await service.QueryAsync(new ReviewQueryContext("first", 1, "Broker|1"), filter, () => 1);
        var second = await service.QueryAsync(new ReviewQueryContext("second", 1, "Broker|1"), filter, () => 1);
        repository.Version = repository.Version with { MetadataVersion = repository.Version.MetadataVersion + 1 };
        var changed = await service.QueryAsync(new ReviewQueryContext("changed", 1, "Broker|1"), filter, () => 1);

        Assert.Equal(2, repository.WorkspaceLoadCount);
        Assert.Same(first.Snapshot, second.Snapshot);
        Assert.NotSame(second.Snapshot, changed.Snapshot);
        Assert.Equal(repository.Version.Token, changed.Snapshot.Version.Token);
    }

    [Fact]
    public async Task Query_ProjectsNeedsReviewOnlyWhenThatTradesFrozenBasisChanges()
    {
        var repository = FakeRepository.Create();
        var assessment = new TradeRuleAssessment(new TradeKey("Broker|1", 1), "playbook:v1", "entry",
            RuleAssessmentStatus.Passed, ReviewEvidenceSource.UserBackfill, "原评价", "", 1, Now);
        var deal = new DealRecord(1, 1, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
            1m, 101m, 10m, 0m, 0m, 0m, Now);
        repository.Assessments.Add(assessment);
        repository.Deals.Add(deal);
        var calculator = new ReviewWorkspaceCalculator();
        var basis = calculator.BuildTradeReviewBasis(repository.Trades[0], [deal], [assessment], null);
        repository.Document = Document(ReviewCompletionStatus.Reviewed, 1, "保留的结论") with
        {
            SourceVersion = basis.SourceVersion,
            RuleVersion = basis.RuleVersion,
            ReviewedSourceVersion = basis.SourceVersion,
            ReviewedRuleVersion = basis.RuleVersion,
        };
        repository.Version = repository.Version with { RuleVersion = "global-rule-v99" };
        var service = new ReviewQueryService(repository);

        var stillReviewed = await service.QueryAsync(
            new ReviewQueryContext("same-trade", 1, "Broker|1"),
            new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30),
                Status: ReviewCompletionStatus.Reviewed), () => 1);
        repository.Deals[0] = deal with { Commission = -1m };
        repository.Version = repository.Version with { SourceVersion = repository.Version.SourceVersion + 1 };
        var needsReview = await service.QueryAsync(
            new ReviewQueryContext("changed-trade", 1, "Broker|1"),
            new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30),
                Status: ReviewCompletionStatus.NeedsReview), () => 1);

        Assert.Equal(1, stillReviewed.Snapshot.TotalCount);
        Assert.Equal(ReviewCompletionStatus.Reviewed, stillReviewed.Snapshot.Documents[1].Status);
        Assert.Equal(1, needsReview.Snapshot.TotalCount);
        Assert.Equal("保留的结论", needsReview.Snapshot.Documents[1].Summary);
        Assert.Equal(ReviewCompletionStatus.NeedsReview, needsReview.Snapshot.Documents[1].Status);
        Assert.Equal(basis.SourceVersion, needsReview.Snapshot.Documents[1].ReviewedSourceVersion);
        Assert.Equal(ReviewCompletionStatus.Reviewed, repository.Document.Status);
    }

    [Fact]
    public async Task Query_ComparesComplianceWithExactSameSampleAndAllocatesOnlyItsDealFees()
    {
        var repository = FakeRepository.Create();
        repository.Trades.Add(Trade(2, -5m));
        repository.Assessments.Add(new TradeRuleAssessment(new TradeKey("Broker|1", 1), "p", "r",
            RuleAssessmentStatus.Passed, ReviewEvidenceSource.UserBackfill, "", "", 1, Now));
        repository.Assessments.Add(new TradeRuleAssessment(new TradeKey("Broker|1", 2), "p", "r",
            RuleAssessmentStatus.Failed, ReviewEvidenceSource.UserBackfill, "", "", 1, Now));
        repository.Deals.Add(new DealRecord(1, 1, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
            1m, 100m, 10m, -1m, 0m, 0m, Now));
        repository.Deals.Add(new DealRecord(2, 2, 2, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
            1m, 100m, -5m, -0.5m, 0m, 0m, Now));
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30),
            ComparisonMode: ReviewComparisonMode.Compliance);

        var result = await new ReviewQueryService(repository).QueryAsync(
            new ReviewQueryContext("analysis", 1, "Broker|1"), filter, () => 1);

        var comparison = result.Snapshot.PeriodComparison!;
        Assert.Equal([1L], comparison.LeftTrades!.Select(item => item.PositionId).ToArray());
        Assert.Equal([2L], comparison.RightTrades!.Select(item => item.PositionId).ToArray());
        Assert.Equal(0, comparison.OverlapCount);
        Assert.Equal(-1.5m, result.Snapshot.Fees!.Commission);
        Assert.Equal(2, result.Snapshot.RiskSamples!.Count);
        Assert.All(result.Snapshot.RiskSamples, item => Assert.False(item.HasReliableExcursion));
    }

    [Fact]
    public async Task Query_IncludesEquityAndNamedSessionAnalysisFromWorkspaceInputs()
    {
        var repository = FakeRepository.Create();
        repository.EquitySamples.AddRange([
            new EquitySample("Broker|1", new DateOnly(2026, 9, 6), Now.AddMinutes(-10), 1_000m, 1_000m, 0m, false),
            new EquitySample("Broker|1", new DateOnly(2026, 9, 6), Now, 1_100m, 1_100m, 0m, false),
        ]);
        repository.CashFlows.Add(new AccountCashFlow("Broker|1", 10, "balance", 100m, Now.AddMinutes(-5)));
        repository.TradingSessions.Add(new TradingSessionDefinition(
            "all-day", "Broker|1", "全天", "UTC", TimeOnly.MinValue, TimeOnly.MinValue,
            [], 0, true, 1, Now, Now));
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30));

        var result = await new ReviewQueryService(repository).QueryAsync(
            new ReviewQueryContext("equity-session", 1, "Broker|1"), filter, () => 1);

        Assert.Equal(0m, result.Snapshot.EquityAnalysis!.CashFlowAdjustedMaximumDrawdownPercentage);
        Assert.Equal("全天", Assert.Single(result.Snapshot.TradingSessionPerformance!).Group);
        Assert.Equal([1L], result.Snapshot.TradingSessionPerformance![0].Trades!.Select(item => item.PositionId));
    }

    [Fact]
    public async Task TradingSession_RejectsUnknownTimeZoneAndUsesOptimisticRevision()
    {
        var repository = FakeRepository.Create();
        var service = new TradingSessionService(repository);
        var session = new TradingSessionDefinition(
            "s1", "Broker|1", "纽约早盘", "invalid/time-zone", new TimeOnly(9, 30), new TimeOnly(10, 30),
            [], 0, true, 0, Now, Now);

        var invalid = await service.SaveAsync(session, 0);
        var saved = await service.SaveAsync(session with { TimeZoneId = "UTC" }, 0);
        var stale = await service.SaveAsync(session with { TimeZoneId = "UTC", Revision = 0 }, 0);

        Assert.Equal(ReviewSaveStatus.ValidationFailed, invalid.Status);
        Assert.True(saved.IsSaved);
        Assert.Equal(1, saved.Value!.Revision);
        Assert.Equal(ReviewSaveStatus.Conflict, stale.Status);
    }

    [Fact]
    public async Task ReviewCache_ClearsOnlyCurrentAccountMarketPayload()
    {
        var repository = FakeRepository.Create();
        var range = new MarketDataRange("cache", "terminal", "Broker|1", "XAUUSD.s", "M1",
            Now.AddMinutes(-1), Now, Now.AddMinutes(-1), Now, MarketDataPrecision.Bars,
            MarketCoverageStatus.Complete, "1", "", Now);
        repository.CachedMarketData = (range,
            [new MarketBar("terminal", "Broker|1", "XAUUSD.s", "M1", Now.AddMinutes(-1), 1, 2, 1, 2, 1, 1, 0)], []);

        var result = await new ReviewCacheService(repository).ClearMarketDataAsync("Broker|1");

        Assert.Equal(2, result.TotalCount);
        Assert.Null(repository.CachedMarketData);
    }

    [Fact]
    public async Task BehaviorReview_UpdatesOnlyHumanFieldsAndRejectsRecalculationDelivery()
    {
        var repository = FakeRepository.Create();
        var occurrence = new BehaviorOccurrence(
            "behavior-1", "Broker|1", new DateOnly(2026, 9, 6), BehaviorRuleKind.CooldownViolation,
            "rule-v1", 2m, 1m, 1.5m, BehaviorRiskLevel.Attention,
            ReviewEvidenceSource.LiveObservation, Now.AddMinutes(-1), Now, "触发事实", "",
            true, "Delivered", null,
            [new BehaviorTradeLink(new TradeKey("Broker|1", 1), BehaviorTradeRole.Trigger)]);
        repository.Behaviors.Add(occurrence);
        var service = new BehaviorReviewService(repository, new FixedTimeProvider(Now));

        var saved = await service.SaveReviewAsync("Broker|1", occurrence.Id, "当时急于追回亏损", true, 0);
        var stored = Assert.Single(repository.Behaviors);

        Assert.True(saved.IsSaved);
        Assert.Equal("当时急于追回亏损", stored.UserExplanation);
        Assert.True(stored.EvidenceInsufficient);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(occurrence.RuleVersion, stored.RuleVersion);
        Assert.Equal(occurrence.EventAtUtc, stored.EventAtUtc);
        Assert.Equal(occurrence.TradeLinks, stored.TradeLinks);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordOccurrenceAsync(
            occurrence with { Source = ReviewEvidenceSource.RuleRecalculation }));
    }

    [Fact]
    public async Task Campaign_RejectsMixedSymbolAndAlreadyGroupedMember()
    {
        var repository = FakeRepository.Create();
        repository.Trades.Add(Trade(2, 1m) with { Symbol = "EURUSD" });
        var service = new CampaignService(repository, new FixedTimeProvider(Now));
        var mixed = new TradeCampaign("c1", "Broker|1", "XAUUSD.s", "混合", "", [1, 2], 0, default, default);

        var result = await service.SaveAsync(mixed, 0);

        Assert.Equal(ReviewSaveStatus.ValidationFailed, result.Status);
    }

    [Fact]
    public async Task Opportunity_ValidatesLinksUpdatesRevisionAndStaysOutsideTradeSample()
    {
        var repository = FakeRepository.Create();
        repository.Playbooks.Add(new PlaybookVersion(
            "pb-v1", "pb", "Broker|1", 1, "突破", "XAUUSD.s", "趋势", "跌回区间", [], Now, Now, true));
        repository.Trades.Add(Trade(2, 5m) with { AccountKey = "Broker|2" });
        var service = new OpportunityService(repository, new FixedTimeProvider(Now));
        var opportunity = new OpportunityRecord(
            "o1", "Broker|1", OpportunityRecordKind.ObservedBeforeMove, Now.AddMinutes(-5), Now,
            new DateOnly(2026, 9, 6), " XAUUSD.s ", TradeSide.Buy, "missing", 100m, 99m, 103m,
            "主动休息", "观察", "1", 0, " 突破后回踩 ");

        var invalidPlaybook = await service.SaveAsync(opportunity, 0);
        var crossAccountTrade = await service.SaveAsync(opportunity with { PlaybookVersionId = "pb-v1", LinkedTradeKey = "2" }, 0);
        var created = await service.SaveAsync(opportunity with { PlaybookVersionId = "pb-v1" }, 0);
        var updated = await service.SaveAsync(created.Value! with { Notes = "后续验证" }, created.Value!.Revision);
        var query = await new ReviewQueryService(repository).QueryAsync(
            new ReviewQueryContext("q", 1, "Broker|1"),
            new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30)), () => 1);

        Assert.Equal(ReviewSaveStatus.ValidationFailed, invalidPlaybook.Status);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, crossAccountTrade.Status);
        Assert.True(created.IsSaved);
        Assert.Equal(1, created.Value!.Revision);
        Assert.Equal("突破后回踩", created.Value.Conditions);
        Assert.Equal(2, updated.Value!.Revision);
        Assert.Equal("后续验证", Assert.Single(repository.Opportunities).Notes);
        Assert.Single(query.Snapshot.Trades);
        Assert.Equal(10m, query.Snapshot.Analytics.Performance.NetPnl);
    }

    [Fact]
    public async Task BulkEdit_NormalizesTagsRejectsCrossAccountAndEnforcesReviewCompletion()
    {
        var repository = FakeRepository.Create();
        repository.Trades.Add(Trade(2, -2m));
        repository.Trades.Add(Trade(3, 1m) with { AccountKey = "Broker|2" });
        repository.Metadata[1] = new TradeReviewMetadata(
            "Broker|1", 1, null, PlanComplianceStatus.Unclassified, "旧策略", "", ["旧"], true, Now.AddDays(-1));
        var service = new ReviewBulkEditService(repository, new FixedTimeProvider(Now));

        var tags = await service.ApplyAsync(new ReviewBulkEditRequest(
            "Broker|1", [1, 2], ReviewBulkEditKind.Tags, " Early, early、Focus "));
        var crossAccount = await service.ApplyAsync(new ReviewBulkEditRequest(
            "Broker|1", [3], ReviewBulkEditKind.Strategy, "突破"));
        repository.Document = Document(ReviewCompletionStatus.Draft, 1, "已有总结");
        repository.Assessments.Add(new TradeRuleAssessment(
            new TradeKey("Broker|1", 1), "p", "r", RuleAssessmentStatus.Passed,
            ReviewEvidenceSource.UserBackfill, "", "", 1, Now));
        var reviewed = await service.ApplyAsync(new ReviewBulkEditRequest(
            "Broker|1", [1, 2], ReviewBulkEditKind.Status, ReviewCompletionStatus.Reviewed.ToString()));

        Assert.True(tags.IsSaved);
        Assert.Equal(2, tags.Value!.AffectedCount);
        Assert.All(repository.LastBulkWrites, item => Assert.Equal(["Early", "Focus"], item.Metadata!.Tags));
        Assert.Equal(ReviewSaveStatus.ValidationFailed, crossAccount.Status);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, reviewed.Status);
        Assert.Contains("#2", reviewed.Message);
    }

    [Fact]
    public async Task BulkEdit_ReviewedFreezesVersionsAndPreservesExistingConclusion()
    {
        var repository = FakeRepository.Create();
        repository.Document = Document(ReviewCompletionStatus.Draft, 2, "已有总结");
        repository.Assessments.Add(new TradeRuleAssessment(
            new TradeKey("Broker|1", 1), "p", "r", RuleAssessmentStatus.Passed,
            ReviewEvidenceSource.UserBackfill, "", "", 1, Now));
        var service = new ReviewBulkEditService(repository, new FixedTimeProvider(Now));

        var result = await service.ApplyAsync(new ReviewBulkEditRequest(
            "Broker|1", [1], ReviewBulkEditKind.Status, ReviewCompletionStatus.Reviewed.ToString()));

        Assert.True(result.IsSaved);
        var write = Assert.Single(repository.LastBulkWrites);
        Assert.Equal(2, write.ExpectedDocumentRevision);
        Assert.Equal(ReviewCompletionStatus.Reviewed, write.Document!.Status);
        Assert.Equal("已有总结", write.Document.Summary);
        var expectedBasis = new ReviewWorkspaceCalculator().BuildTradeReviewBasis(
            repository.Trades[0], repository.Deals, repository.Assessments, null);
        Assert.Equal(expectedBasis.SourceVersion, write.Document.ReviewedSourceVersion);
        Assert.Equal(expectedBasis.RuleVersion, write.Document.ReviewedRuleVersion);
        Assert.Equal(Now, write.Document.ReviewedAtUtc);
    }

    [Fact]
    public async Task SuggestedTag_AcceptAndUndoPreserveOtherManualTags()
    {
        var repository = FakeRepository.Create();
        repository.Metadata[1] = new TradeReviewMetadata(
            "Broker|1", 1, null, PlanComplianceStatus.Unclassified,
            string.Empty, string.Empty, ["人工标签"], true, Now.AddMinutes(-1));
        var service = new ReviewTagSuggestionService(repository, new FixedTimeProvider(Now));

        var accepted = await service.ApplyAsync(new TradeKey("Broker|1", 1), "计划偏离", true);
        var undone = await service.ApplyAsync(new TradeKey("Broker|1", 1), "计划偏离", false);

        Assert.True(accepted.IsSaved);
        Assert.True(undone.IsSaved);
        Assert.Equal(["人工标签"], repository.Metadata[1].Tags);
        Assert.True(repository.Metadata[1].UserEdited);
    }

    [Fact]
    public async Task Replay_RejectsResponseForAnotherAccount()
    {
        var repository = FakeRepository.Create();
        var source = new FakeHistorySource(resultAccount: "Broker|2");
        var service = new TradeReplayService(repository, source);
        var request = new MarketHistoryRequest("request", "terminal", "Broker|1", "XAUUSD.s", "M1", Now, Now.AddMinutes(1), MarketDataPrecision.Bars);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(request));
    }

    [Fact]
    public async Task Replay_DoesNotReuseCompleteBarCacheForTickRequest()
    {
        var repository = FakeRepository.Create();
        repository.CachedMarketData = (
            new MarketDataRange("bar-cache", "terminal", "Broker|1", "XAUUSD.s", "M1",
                Now, Now.AddMinutes(1), Now, Now.AddMinutes(1), MarketDataPrecision.Bars,
                MarketCoverageStatus.Complete, "bars", "", Now),
            [], []);
        var source = new FakeHistorySource("Broker|1");
        var request = new MarketHistoryRequest(
            "tick-request", "terminal", "Broker|1", "XAUUSD.s", "M1",
            Now, Now.AddMinutes(1), MarketDataPrecision.Ticks);

        var result = await new TradeReplayService(repository, source).LoadAsync(request);

        Assert.Equal(1, source.CallCount);
        Assert.Equal(MarketDataPrecision.Ticks, repository.LastMarketPrecision);
        Assert.Equal(MarketDataPrecision.Ticks, result.Range.Precision);
    }

    [Fact]
    public async Task Export_EscapesSpreadsheetAndHtml_UsesStringIdsAndHidesPublicAccount()
    {
        var repository = FakeRepository.Create();
        repository.Trades[0] = repository.Trades[0] with { Symbol = "=cmd", PositionId = 9_007_199_254_740_993 };
        var trade = repository.Trades[0];
        var document = Document(ReviewCompletionStatus.Reviewed, 1, "<img src=x onerror=alert(1)>") with
        {
            TradeKey = new TradeKey(trade.AccountKey, trade.PositionId),
        };
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30));
        var snapshot = new ReviewWorkspaceSnapshot(filter,
            new TradePet.Core.Trading.ReviewAnalyticsCalculator().Calculate(
                new ReviewFilter("Broker|1", filter.FromServerDate, filter.ToServerDate), [trade]),
            [trade], new Dictionary<long, TradeReviewDocument> { [trade.PositionId] = document }, [], [], [],
            new ReviewDataQuality(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
            repository.Version, 1, 1, 100);
        var deal = new DealRecord(9_007_199_254_740_992, 9_007_199_254_740_991, trade.PositionId,
            trade.Symbol, trade.Side, DealEntryKind.In, 1m, 100m, 0m, 0m, 0m, 0m, Now.AddDays(-1));
        var detail = new TradeDetailSnapshot(trade, [deal], null, document, null, [], null, null,
            [], [], [], null, repository.Version.Token);
        repository.Opportunities.Add(new OpportunityRecord(
            "o-export", "Broker|1", OpportunityRecordKind.DeliberatelySkipped, Now, Now,
            new DateOnly(2026, 9, 6), "XAUUSD.s", null, null, null, null, null,
            "主动休息", "未交易", null, 1, "只在确认后参与"));
        repository.OpportunityAttachments.Add(new ReviewAttachment(
            "a-export", "Broker|1", new string('a', 64), "机会.png", "image/png", 4,
            "private/path.png", "机会截图", "opportunity", "o-export",
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, Now, Now, ReviewTimeBasis.Local, "manual"), Now,
            "突破确认"));
        var workspace = await repository.LoadWorkspaceAsync("Broker|1", filter.FromServerDate, filter.ToServerDate);

        var service = new ReviewExportService();
        var package = service.Build(snapshot, [detail], workspace, ReviewExportMode.PublicShare);
        var localPackage = service.Build(snapshot, [detail], workspace, ReviewExportMode.LocalArchive);
        var csv = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "trades.csv").Content);
        var html = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "review.html").Content);
        var json = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "review-data.json").Content);
        var manifest = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "manifest.json").Content);
        var localJson = System.Text.Encoding.UTF8.GetString(localPackage.Entries.Single(item => item.Path == "review-data.json").Content);
        var localCsv = System.Text.Encoding.UTF8.GetString(localPackage.Entries.Single(item => item.Path == "trades.csv").Content);
        using var jsonDocument = System.Text.Json.JsonDocument.Parse(json);

        Assert.Contains("\"'=cmd\"", csv);
        Assert.Contains("\"T000001\"", csv);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("\"id\":\"T000001\"", json);
        Assert.DoesNotContain("9007199254740993", json);
        Assert.DoesNotContain("9007199254740992", json);
        var exportedOpportunity = jsonDocument.RootElement.GetProperty("opportunities")[0];
        Assert.Equal("只在确认后参与", exportedOpportunity.GetProperty("conditions").GetString());
        Assert.Equal("O000001", exportedOpportunity.GetProperty("id").GetString());
        Assert.Equal(0, jsonDocument.RootElement.GetProperty("attachments").GetArrayLength());
        Assert.DoesNotContain("o-export", json);
        Assert.DoesNotContain("a-export", json);
        Assert.DoesNotContain("private/path.png", json);
        Assert.DoesNotContain("Broker|1", json);
        Assert.DoesNotContain("Broker|1", manifest);
        Assert.Contains("\"'9007199254740993\"", localCsv);
        Assert.Contains("\"positionId\":\"9007199254740993\"", localJson);
        Assert.Contains("\"ticket\":\"9007199254740992\"", localJson);
        Assert.Throws<InvalidDataException>(() => service.Build(
            snapshot, [detail], workspace with { Opportunities = [] },
            ReviewExportMode.PublicShare, includedAttachments: [repository.OpportunityAttachments[0]]));
    }

    [Fact]
    public void Export_CsvNeutralizesFormulaAfterWhitespaceAndLineBreak()
    {
        var trade = Trade(1, 10m) with { Symbol = "\t=HYPERLINK(\"x\")" };
        var document = Document(ReviewCompletionStatus.Draft, 1, "\r\n+cmd");
        var filter = new ReviewWorkspaceFilter("Broker|1", new(2026, 9, 1), new(2026, 9, 30));
        var version = new ReviewDataVersion("Broker|1", 1, 1, 1, "rule-1", "time-1", Now);
        var snapshot = new ReviewWorkspaceSnapshot(filter,
            new TradePet.Core.Trading.ReviewAnalyticsCalculator().Calculate(
                new ReviewFilter("Broker|1", filter.FromServerDate, filter.ToServerDate), [trade]),
            [trade], new Dictionary<long, TradeReviewDocument> { [1] = document }, [], [], [],
            new ReviewDataQuality(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
            version, 1, 1, 100);
        var detail = new TradeDetailSnapshot(trade, [], null, document, null, [], null, null,
            [], [], [], null, version.Token);

        var package = new ReviewExportService(new FixedTimeProvider(Now)).Build(snapshot, [detail]);
        var csv = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "trades.csv").Content);

        Assert.Contains("\"'\t=HYPERLINK", csv);
        Assert.Contains("\"'\r\n+cmd\"", csv);
    }

    [Fact]
    public void Export_UsesAllFilteredTradesInsteadOfOnlyCurrentPageForCsvAndHtml()
    {
        var first = Trade(1, 10m) with { Symbol = "PAGE-1" };
        var second = Trade(2, -5m) with { Symbol = "PAGE-2" };
        var filter = new ReviewWorkspaceFilter(
            "Broker|1", new(2026, 9, 1), new(2026, 9, 30), Page: 1, PageSize: 1);
        var version = new ReviewDataVersion("Broker|1", 1, 1, 1, "rule-1", "time-1", Now);
        var secondDocument = Document(ReviewCompletionStatus.Reviewed, 1, "第二页结论") with
        {
            TradeKey = new TradeKey(second.AccountKey, second.PositionId),
        };
        var snapshot = new ReviewWorkspaceSnapshot(
            filter,
            new TradePet.Core.Trading.ReviewAnalyticsCalculator().Calculate(
                new ReviewFilter("Broker|1", filter.FromServerDate, filter.ToServerDate), [first, second]),
            [first], new Dictionary<long, TradeReviewDocument>(), [], [], [],
            new ReviewDataQuality(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
            version, 2, 1, 1,
            AllFilteredTrades: [first, second]);
        var details = new[]
        {
            new TradeDetailSnapshot(first, [], null, null, null, [], null, null, [], [], [], null, version.Token),
            new TradeDetailSnapshot(second, [], null, secondDocument, null, [], null, null, [], [], [], null, version.Token),
        };

        var package = new ReviewExportService(new FixedTimeProvider(Now)).Build(snapshot, details);
        var csv = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "trades.csv").Content);
        var html = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "review.html").Content);
        var json = System.Text.Encoding.UTF8.GetString(package.Entries.Single(item => item.Path == "review-data.json").Content);

        Assert.Contains("PAGE-1", csv);
        Assert.Contains("PAGE-2", csv);
        Assert.Contains("第二页结论", csv);
        Assert.Contains("PAGE-2", html);
        Assert.Contains("第二页结论", html);
        using var data = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, data.RootElement.GetProperty("trades").GetArrayLength());
    }

    [Fact]
    public async Task Export_All251AndExplicitSelectionKeepCountNetAndRevisionAlignedAcrossFormats()
    {
        var repository = FakeRepository.Create();
        for (var positionId = 2; positionId <= 251; positionId++)
        {
            repository.Trades.Add(Trade(positionId, positionId));
        }
        var filter = new ReviewWorkspaceFilter(
            "Broker|1", new(2026, 9, 1), new(2026, 9, 30), PageSize: 100);
        var query = await new ReviewQueryService(repository).QueryAsync(
            new ReviewQueryContext("251-export", 1, "Broker|1"), filter, () => 1);
        var all = query.Snapshot.AllFilteredTrades!;
        var allDetails = all.Select(trade => new TradeDetailSnapshot(
            trade, [], null, null, null, [], null, null, [], [], [], null,
            query.Snapshot.Version.Token)).ToArray();
        var service = new ReviewExportService(new FixedTimeProvider(Now));

        var allPackage = service.Build(query.Snapshot, allDetails, query.Data);
        var allCsv = System.Text.Encoding.UTF8.GetString(allPackage.Entries.Single(item => item.Path == "trades.csv").Content);
        var allHtml = System.Text.Encoding.UTF8.GetString(allPackage.Entries.Single(item => item.Path == "review.html").Content);
        var allMarkdown = System.Text.Encoding.UTF8.GetString(allPackage.Entries.Single(item => item.Path == "review.md").Content);
        using var allData = System.Text.Json.JsonDocument.Parse(allPackage.Entries.Single(item => item.Path == "review-data.json").Content);
        using var allManifest = System.Text.Json.JsonDocument.Parse(allPackage.Entries.Single(item => item.Path == "manifest.json").Content);

        Assert.Equal(100, query.Snapshot.Trades.Count);
        Assert.Equal(251, allCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Equal(251, System.Text.RegularExpressions.Regex.Matches(allHtml, "<tr><td>").Count);
        Assert.Equal(251, allData.RootElement.GetProperty("trades").GetArrayLength());
        Assert.Equal(251, allManifest.RootElement.GetProperty("tradeCount").GetInt32());
        Assert.Equal(31_635m, allManifest.RootElement.GetProperty("netPnl").GetDecimal());
        Assert.Contains("完整交易：251", allMarkdown);
        Assert.Contains("净盈亏：31635", allMarkdown);

        var selectedTrades = new[] { all.First(item => item.PositionId == 1), all.First(item => item.PositionId == 251) };
        var selectedSnapshot = query.Snapshot with { AllFilteredTrades = selectedTrades, TotalCount = 2 };
        var selectedDetails = allDetails.Where(item => item.Trade.PositionId is 1 or 251).ToArray();
        var selectedPackage = service.Build(selectedSnapshot, selectedDetails, query.Data,
            ReviewExportMode.PublicShare, ReviewExportScope.SelectedTrades);
        var selectedCsv = System.Text.Encoding.UTF8.GetString(selectedPackage.Entries.Single(item => item.Path == "trades.csv").Content);
        var selectedHtml = System.Text.Encoding.UTF8.GetString(selectedPackage.Entries.Single(item => item.Path == "review.html").Content);
        var selectedMarkdown = System.Text.Encoding.UTF8.GetString(selectedPackage.Entries.Single(item => item.Path == "review.md").Content);
        using var selectedData = System.Text.Json.JsonDocument.Parse(selectedPackage.Entries.Single(item => item.Path == "review-data.json").Content);
        using var selectedManifest = System.Text.Json.JsonDocument.Parse(selectedPackage.Entries.Single(item => item.Path == "manifest.json").Content);

        Assert.Equal(2, selectedCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(selectedHtml, "<tr><td>").Count);
        Assert.Equal(2, selectedData.RootElement.GetProperty("trades").GetArrayLength());
        Assert.Equal(2, selectedManifest.RootElement.GetProperty("tradeCount").GetInt32());
        Assert.Equal(261m, selectedManifest.RootElement.GetProperty("netPnl").GetDecimal());
        Assert.Equal("SelectedTrades", selectedManifest.RootElement.GetProperty("exportScope").GetString());
        Assert.Contains("完整交易：2", selectedMarkdown);
        Assert.Contains("净盈亏：261", selectedMarkdown);
    }

    [Fact]
    public void Export_PublicAliasesNestedIdsAndHidesAccountServerAndMachinePath()
    {
        const string account = "BrokerSecret|12345";
        var trade = Trade(9_007_199_254_740_993, 7m) with { AccountKey = account };
        var key = new TradeKey(account, trade.PositionId);
        var document = Document(ReviewCompletionStatus.Reviewed, 3,
            "BrokerSecret|12345 C:\\Users\\SampleUser\\private.txt") with { TradeKey = key };
        var plan = new StructuredTradePlan(
            "raw-plan-BrokerSecret|12345", account, new(2026, 9, 6), trade.Symbol, trade.Side,
            100m, null, null, 99m, 102m, "BrokerSecret", "setup", [], "C:\\Users\\SampleUser\\plan.txt",
            true, Now, Now);
        var behavior = new BehaviorOccurrence(
            "raw-behavior-BrokerSecret|12345", account, new(2026, 9, 6),
            BehaviorRuleKind.OvertradeBurst, "raw-rule-BrokerSecret|12345", 7m, 5m, 6m,
            BehaviorRiskLevel.Attention, ReviewEvidenceSource.LiveObservation, Now, Now,
            "BrokerSecret|12345 C:\\Users\\SampleUser\\log.txt", string.Empty, false, "", null,
            [new BehaviorTradeLink(key, BehaviorTradeRole.Trigger)]);
        var attachment = new ReviewAttachment(
            "raw-attachment-BrokerSecret|12345", account, new string('a', 64),
            "BrokerSecret-account.png", "image/png", 5, "C:/Users/SampleUser/private.png",
            "BrokerSecret|12345", "trade", trade.PositionId.ToString(),
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, Now, Now,
                ReviewTimeBasis.Local, "raw-source-BrokerSecret|12345"), Now);
        var filter = new ReviewWorkspaceFilter(account, new(2026, 9, 1), new(2026, 9, 30));
        var version = new ReviewDataVersion(account, 1, 1, 1,
            "raw-rule-BrokerSecret|12345", "C:\\Users\\SampleUser\\time", Now);
        var snapshot = new ReviewWorkspaceSnapshot(
            filter,
            new TradePet.Core.Trading.ReviewAnalyticsCalculator().Calculate(
                new ReviewFilter(account, filter.FromServerDate, filter.ToServerDate), [trade]),
            [trade], new Dictionary<long, TradeReviewDocument> { [trade.PositionId] = document },
            [], [], [behavior],
            new ReviewDataQuality(1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
            version, 1, 1, 100, AllFilteredTrades: [trade]);
        var detail = new TradeDetailSnapshot(trade, [], null, document, null, [], plan, null,
            [], [behavior], [attachment], null, version.Token);

        var package = new ReviewExportService(new FixedTimeProvider(Now)).Build(
            snapshot, [detail], mode: ReviewExportMode.PublicShare,
            includedAttachments: [attachment]);
        var text = string.Join("\n", package.Entries.Select(item => System.Text.Encoding.UTF8.GetString(item.Content)));
        using var data = System.Text.Json.JsonDocument.Parse(
            package.Entries.Single(item => item.Path == "review-data.json").Content);

        Assert.DoesNotContain(account, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BrokerSecret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-plan", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-behavior", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-attachment", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("9007199254740993", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("T000001", data.RootElement.GetProperty("trades")[0].GetProperty("id").GetString());
        Assert.Equal("P000001", data.RootElement.GetProperty("plans")[0].GetProperty("id").GetString());
        Assert.Equal("B000001", data.RootElement.GetProperty("behaviors")[0].GetProperty("id").GetString());
        Assert.Equal("A000001", data.RootElement.GetProperty("attachments")[0].GetProperty("id").GetString());
        Assert.Equal("attachments/A000001/asset.png",
            data.RootElement.GetProperty("attachments")[0].GetProperty("exportPath").GetString());
        Assert.Equal(3, data.RootElement.GetProperty("revisionTotal").GetInt32());
        Assert.Equal(3, data.RootElement.GetProperty("trades")[0]
            .GetProperty("review").GetProperty("Revision").GetInt32());
        Assert.All(package.Entries.Where(item => item.Path is "review.md" or "review.html" or "manifest.json"),
            item => Assert.Contains("3", System.Text.Encoding.UTF8.GetString(item.Content)));
    }

    [Fact]
    public async Task ImprovementGoal_DoesNotEnableAutomaticReminderForSubjectiveMeasurement()
    {
        var repository = FakeRepository.Create();
        var service = new ImprovementService(repository, new FixedTimeProvider(Now));
        var goal = new ImprovementGoal("g", "Broker|1", "保持平静", null, "r1", new(2026, 9, 6), null,
            null, null, "每天主观记录", "", true, ImprovementGoalStatus.Active, 0, default, default);

        var result = await service.SaveAsync(goal, 0);

        Assert.Equal(ReviewSaveStatus.ValidationFailed, result.Status);
    }

    [Fact]
    public async Task ImprovementGoal_ArchiveStopsNotificationsAndKeepsExistingIdentity()
    {
        var repository = FakeRepository.Create();
        var goal = new ImprovementGoal("g1", "Broker|1", "减少追单", BehaviorRuleKind.ReentryCount,
            "rule-1", new(2026, 9, 1), null, 1m, 3m, "每日追单次数", "XAUUSD.s", true,
            ImprovementGoalStatus.Active, 2, Now.AddDays(-5), Now.AddDays(-1));
        repository.Goals.Add(goal);
        var service = new ImprovementService(repository, new FixedTimeProvider(Now));

        var result = await service.ArchiveAsync(goal, new DateOnly(2026, 9, 6));

        Assert.True(result.IsSaved);
        Assert.Equal("g1", result.Value!.Id);
        Assert.Equal(3, result.Value.Revision);
        Assert.Equal(ImprovementGoalStatus.Archived, result.Value.Status);
        Assert.Equal(new DateOnly(2026, 9, 6), result.Value.EndServerDate);
        Assert.False(result.Value.NotificationEnabled);
    }

    [Fact]
    public async Task ImprovementGoal_NewMeasurementVersionFreezesBaselineAndArchivesPreviousVersion()
    {
        var repository = FakeRepository.Create();
        var current = new ImprovementGoal(
            "goal:v1", "Broker|1", "减少追单", BehaviorRuleKind.ReentryCount, "rule-1",
            new DateOnly(2026, 9, 1), null, 80m, null, "无追单执行率", "XAUUSD.s", true,
            ImprovementGoalStatus.Active, 2, Now.AddDays(-5), Now.AddDays(-1),
            GoalKey: "goal", Version: 1, ObservationWindowDays: 7);
        repository.Goals.Add(current);
        var observations = new[]
        {
            new GoalObservation("o1", current.Id, current.AccountKey, new DateOnly(2026, 9, 4),
                2, 1, 1, GoalObservationStatus.Failed, "e1", Now, ["e1"], current.RuleVersion),
            new GoalObservation("o2", current.Id, current.AccountKey, new DateOnly(2026, 9, 5),
                1, 1, 0, GoalObservationStatus.Passed, "e2", Now, ["e2"], current.RuleVersion),
        };
        var candidate = current with
        {
            Id = "temporary",
            TargetValue = 90m,
            StartServerDate = new DateOnly(2026, 9, 6),
            Revision = 0,
        };

        var result = await new ImprovementService(repository, new FixedTimeProvider(Now))
            .CreateVersionAsync(current, candidate, observations);

        Assert.True(result.IsSaved);
        Assert.Equal("goal:v2", result.Value!.Id);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(3, result.Value.BaselineOpportunityCount);
        Assert.Equal(2, result.Value.BaselinePassCount);
        Assert.Equal(1, result.Value.BaselineFailCount);
        Assert.InRange(result.Value.BaselineValue!.Value, 66.66m, 66.67m);
        Assert.Equal(current.Id, result.Value.PreviousVersionId);
        Assert.Equal(ImprovementGoalStatus.Archived,
            repository.Goals.Single(item => item.Id == current.Id).Status);
        Assert.False(repository.Goals.Single(item => item.Id == current.Id).NotificationEnabled);
    }

    [Fact]
    public async Task GoalObservation_WithNoOpportunityIsNotApplicableAndNeverCountsAsPass()
    {
        var repository = FakeRepository.Create();
        var observation = new GoalObservation(
            "o", "g", "Broker|1", new DateOnly(2026, 9, 6), 0, 1, 0,
            GoalObservationStatus.Passed, "", Now);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new BehaviorReviewService(repository).RecordGoalObservationAsync(observation));

        await new BehaviorReviewService(repository).RecordGoalObservationAsync(
            observation with { PassCount = 0 });
        var stored = Assert.Single(repository.GoalObservations);
        Assert.Equal(GoalObservationStatus.NotApplicable, stored.Status);
        Assert.Equal(0, stored.PassCount);
    }

    private static TradeReviewDocument Document(ReviewCompletionStatus status, int revision, string summary) =>
        new(new TradeKey("Broker|1", 1), status, "", "", "", "", "下次动作", summary, "", "", revision,
            "source-1", "rule-1", null, null, Now, Now, status == ReviewCompletionStatus.Reviewed ? Now : null);

    private static TradeRecord Trade(long id, decimal pnl) => new(
        "Broker|1", id, "XAUUSD.s", TradeSide.Buy, Now.AddDays(-1), Now, new(2026, 9, 5), new(2026, 9, 6),
        100m, 101m, 1m, 1m, 0m, pnl, true);

    private static TradeDetailData CreateDetail(TradeRecord trade) =>
        new(trade, [], null, null, null, [], null, null, [], [], [], null,
            new ReviewDataVersion(trade.AccountKey, 1, 1, 1, "rule-1", "time-1", Now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHistorySource(string resultAccount) : IMarketHistorySource
    {
        public int CallCount { get; private set; }

        public Task<MarketHistoryResult> LoadAsync(MarketHistoryRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var range = new MarketDataRange(request.RequestId, request.TerminalId, resultAccount, request.Symbol, request.Timeframe,
                request.FromUtc, request.ToUtc, request.FromUtc, request.ToUtc, request.Precision,
                MarketCoverageStatus.Complete, "1", "", Now);
            return Task.FromResult(new MarketHistoryResult(range, [], []));
        }
    }

    private sealed class FakeRepository : IReviewWorkspaceRepository
    {
        public List<TradeRecord> Trades { get; } = [];
        public List<DealRecord> Deals { get; } = [];
        public List<TradeRuleAssessment> Assessments { get; } = [];
        public List<TradeCampaign> Campaigns { get; } = [];
        public List<ImprovementGoal> Goals { get; } = [];
        public List<BehaviorOccurrence> Behaviors { get; } = [];
        public List<GoalObservation> GoalObservations { get; } = [];
        public List<PlaybookVersion> Playbooks { get; } = [];
        public List<OpportunityRecord> Opportunities { get; } = [];
        public List<EquitySample> EquitySamples { get; } = [];
        public List<AccountCashFlow> CashFlows { get; } = [];
        public List<TradingSessionDefinition> TradingSessions { get; } = [];
        public List<ReviewAttachment> OpportunityAttachments { get; } = [];
        public Dictionary<DateOnly, DailyJournal> DailyJournals { get; } = [];
        public Dictionary<long, TradeReviewMetadata> Metadata { get; } = [];
        public IReadOnlyList<ReviewBulkWriteItem> LastBulkWrites { get; private set; } = [];
        public TradeReviewDocument? Document { get; set; }
        public TradeDetailData? TradeDetailOverride { get; set; }
        public TaskCompletionSource<bool>? WorkspaceLoadStarted { get; set; }
        public int WorkspaceLoadCount { get; private set; }
        public ReviewDataVersion Version { get; set; } = new("Broker|1", 1, 1, 1, "rule-1", "time-1", Now);
        public (MarketDataRange? Range, IReadOnlyList<MarketBar> Bars, IReadOnlyList<MarketTick> Ticks)? CachedMarketData { get; set; }
        public MarketDataPrecision? LastMarketPrecision { get; private set; }

        public static FakeRepository Create()
        {
            var value = new FakeRepository();
            value.Trades.Add(Trade(1, 10m));
            return value;
        }

        public Task<ReviewDataVersion> LoadReviewDataVersionAsync(string accountKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Version);

        public async Task<ReviewWorkspaceData> LoadWorkspaceAsync(string accountKey, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            WorkspaceLoadCount++;
            if (WorkspaceLoadStarted is not null)
            {
                WorkspaceLoadStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ReviewWorkspaceData(accountKey, "USD", Trades, Deals,
                Metadata,
                Document is null ? new Dictionary<long, TradeReviewDocument>() : new Dictionary<long, TradeReviewDocument> { [Document.TradeKey.PositionId] = Document },
                new Dictionary<long, TradeExcursion>(), DailyJournals, Playbooks, Assessments, Campaigns, Behaviors, Goals, GoalObservations, Opportunities, [], [], Version,
                OpportunityAttachments: OpportunityAttachments,
                EquitySamples: EquitySamples,
                CashFlows: CashFlows,
                TradingSessions: TradingSessions);
        }

        public Task<TradeDetailData?> LoadTradeDetailAsync(TradeKey key, CancellationToken cancellationToken = default)
        {
            if (TradeDetailOverride is not null)
            {
                return Task.FromResult<TradeDetailData?>(TradeDetailOverride);
            }
            var trade = Trades.SingleOrDefault(item => item.PositionId == key.PositionId && item.AccountKey == key.AccountKey);
            Metadata.TryGetValue(key.PositionId, out var metadata);
            return Task.FromResult(trade is null ? null : new TradeDetailData(trade, [], metadata, Document, null, [], null, null,
                Assessments.Where(item => item.TradeKey == key).ToArray(),
                Behaviors.Where(item => item.TradeLinks.Any(link => link.TradeKey == key)).ToArray(), [],
                Campaigns.FirstOrDefault(item => item.PositionIds.Contains(key.PositionId)), Version));
        }

        public Task<TradeReviewDocument?> LoadTradeReviewDocumentAsync(TradeKey key, CancellationToken cancellationToken = default) => Task.FromResult(Document);

        public Task<ReviewSaveResult<TradeReviewDocument>> SaveTradeReviewDocumentAsync(TradeReviewDocument document, int expectedRevision, CancellationToken cancellationToken = default)
        {
            if ((Document?.Revision ?? 0) != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<TradeReviewDocument>.Conflict("revision conflict"));
            }
            Document = document;
            return Task.FromResult(ReviewSaveResult<TradeReviewDocument>.Saved(document));
        }
        public Task<ReviewSaveResult<ReviewBulkEditResult>> SaveBulkReviewAsync(IReadOnlyList<ReviewBulkWriteItem> items, CancellationToken cancellationToken = default)
        {
            LastBulkWrites = items;
            foreach (var item in items)
            {
                if (item.Metadata is not null)
                {
                    Metadata[item.TradeKey.PositionId] = item.Metadata;
                }
                if (item.Document is not null)
                {
                    Document = item.Document;
                }
            }
            return Task.FromResult(ReviewSaveResult<ReviewBulkEditResult>.Saved(new(items.Count)));
        }

        public Task<ReviewSaveResult<DailyJournal>> SaveDailyJournalAsync(DailyJournal journal, int expectedRevision, CancellationToken cancellationToken = default)
        {
            var current = DailyJournals.GetValueOrDefault(journal.ServerDate);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<DailyJournal>.Conflict("revision conflict"));
            }
            var saved = journal with { Revision = expectedRevision + 1 };
            DailyJournals[journal.ServerDate] = saved;
            return Task.FromResult(ReviewSaveResult<DailyJournal>.Saved(saved));
        }
        public Task<ReviewSaveResult<PeriodReview>> SavePeriodReviewAsync(PeriodReview review, int expectedRevision, CancellationToken cancellationToken = default) => Task.FromResult(ReviewSaveResult<PeriodReview>.Saved(review));
        public Task<ReviewSaveResult<PlaybookVersion>> SavePlaybookVersionAsync(PlaybookVersion playbook, CancellationToken cancellationToken = default) => Task.FromResult(ReviewSaveResult<PlaybookVersion>.Saved(playbook));
        public Task<ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>> SaveRuleAssessmentsAsync(TradeKey key, IReadOnlyList<TradeRuleAssessment> assessments, CancellationToken cancellationToken = default) { Assessments.RemoveAll(item => item.TradeKey == key); Assessments.AddRange(assessments); return Task.FromResult(ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>.Saved(assessments)); }
        public Task<ReviewSaveResult<TradeCampaign>> SaveCampaignAsync(TradeCampaign campaign, int expectedRevision, CancellationToken cancellationToken = default) { Campaigns.Add(campaign); return Task.FromResult(ReviewSaveResult<TradeCampaign>.Saved(campaign)); }
        public Task<ReviewSaveResult<ImprovementGoal>> SaveGoalAsync(ImprovementGoal goal, int expectedRevision, CancellationToken cancellationToken = default)
        {
            var existing = Goals.FirstOrDefault(item => item.Id == goal.Id);
            if ((existing?.Revision ?? 0) != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Conflict("revision conflict"));
            }
            Goals.RemoveAll(item => item.Id == goal.Id);
            Goals.Add(goal);
            return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Saved(goal));
        }
        public Task<ReviewSaveResult<ImprovementGoal>> SaveGoalVersionAsync(
            ImprovementGoal previousVersion, int expectedPreviousRevision, ImprovementGoal nextVersion,
            CancellationToken cancellationToken = default)
        {
            var current = Goals.FirstOrDefault(item => item.Id == previousVersion.Id);
            if (current?.Revision != expectedPreviousRevision || Goals.Any(item => item.Id == nextVersion.Id))
            {
                return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Conflict("revision conflict"));
            }
            Goals.Remove(current);
            Goals.Add(previousVersion with { Revision = expectedPreviousRevision + 1 });
            var saved = nextVersion with { Revision = 1 };
            Goals.Add(saved);
            return Task.FromResult(ReviewSaveResult<ImprovementGoal>.Saved(saved));
        }
        public Task SaveGoalObservationAsync(GoalObservation observation, CancellationToken cancellationToken = default)
        {
            GoalObservations.RemoveAll(item => item.Id == observation.Id);
            GoalObservations.Add(observation);
            return Task.CompletedTask;
        }
        public Task SaveBehaviorOccurrenceAsync(BehaviorOccurrence occurrence, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReviewSaveResult<BehaviorOccurrence>> SaveBehaviorReviewAsync(
            string accountKey, string occurrenceId, string? userExplanation, bool evidenceInsufficient,
            int expectedRevision, DateTimeOffset reviewedAtUtc, CancellationToken cancellationToken = default)
        {
            var current = Behaviors.FirstOrDefault(item => item.AccountKey == accountKey && item.Id == occurrenceId);
            if (current is null)
            {
                return Task.FromResult(ReviewSaveResult<BehaviorOccurrence>.Missing("missing"));
            }
            if (current.Revision != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<BehaviorOccurrence>.Conflict("revision conflict"));
            }
            var saved = current with
            {
                UserExplanation = userExplanation,
                EvidenceInsufficient = evidenceInsufficient,
                Revision = expectedRevision + 1,
                HumanReviewedAtUtc = reviewedAtUtc,
            };
            Behaviors.Remove(current);
            Behaviors.Add(saved);
            return Task.FromResult(ReviewSaveResult<BehaviorOccurrence>.Saved(saved));
        }
        public Task<ReviewSaveResult<OpportunityRecord>> SaveOpportunityAsync(OpportunityRecord opportunity, int expectedRevision, CancellationToken cancellationToken = default)
        {
            var existing = Opportunities.FirstOrDefault(item => item.Id == opportunity.Id);
            if ((existing?.Revision ?? 0) != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<OpportunityRecord>.Conflict("revision conflict"));
            }
            Opportunities.RemoveAll(item => item.Id == opportunity.Id);
            Opportunities.Add(opportunity);
            return Task.FromResult(ReviewSaveResult<OpportunityRecord>.Saved(opportunity));
        }
        public Task<ReviewSaveResult<ReviewSavedFilter>> SaveFilterAsync(ReviewSavedFilter filter, int expectedRevision, CancellationToken cancellationToken = default) => Task.FromResult(ReviewSaveResult<ReviewSavedFilter>.Saved(filter));
        public Task<ReviewSaveResult<TradingSessionDefinition>> SaveTradingSessionAsync(TradingSessionDefinition session, int expectedRevision, CancellationToken cancellationToken = default)
        {
            var current = TradingSessions.FirstOrDefault(item => item.Id == session.Id);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return Task.FromResult(ReviewSaveResult<TradingSessionDefinition>.Conflict("revision conflict"));
            }
            var saved = session with { Revision = expectedRevision + 1 };
            TradingSessions.RemoveAll(item => item.Id == session.Id);
            TradingSessions.Add(saved);
            return Task.FromResult(ReviewSaveResult<TradingSessionDefinition>.Saved(saved));
        }
        public Task<IReadOnlyList<ReviewSavedFilter>> LoadFiltersAsync(string accountKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReviewSavedFilter>>([]);
        public Task SaveServerTimeSegmentAsync(ServerTimeSegment segment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveMarketDataAsync(MarketDataRange range, IReadOnlyList<MarketBar> bars, IReadOnlyList<MarketTick> ticks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<(MarketDataRange? Range, IReadOnlyList<MarketBar> Bars, IReadOnlyList<MarketTick> Ticks)> LoadMarketDataAsync(string accountKey, string terminalId, string symbol, string timeframe, MarketDataPrecision precision, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            LastMarketPrecision = precision;
            var cached = CachedMarketData;
            return Task.FromResult(cached is not null && cached.Value.Range?.Precision == precision
                ? cached.Value
                : ((MarketDataRange?)null, (IReadOnlyList<MarketBar>)[], (IReadOnlyList<MarketTick>)[]));
        }
        public Task<ReviewCacheCleanupResult> ClearMarketDataCacheAsync(string accountKey, CancellationToken cancellationToken = default)
        {
            var result = CachedMarketData is { } cached
                ? new ReviewCacheCleanupResult(cached.Range is null ? 0 : 1, cached.Bars.Count, cached.Ticks.Count)
                : new ReviewCacheCleanupResult(0, 0, 0);
            CachedMarketData = null;
            return Task.FromResult(result);
        }
    }
}
