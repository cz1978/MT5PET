using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using TradePet.Application.Review;
using TradePet.Application.Runtime;
using TradePet.App.Runtime;
using TradePet.App.ViewModels.Review;
using TradePet.Core.Domain;
using TradePet.Infrastructure.Persistence;
using Xunit;

namespace TradePet.App.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public void ExportPreview_DefaultsToAllFilteredNoAttachmentsAndInvalidatesOnScopeOrAccountChange()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        Assert.Equal("全部筛选结果", viewModel.ExportScope);
        Assert.False(viewModel.CanConfirmExport);
        var option = new ReviewExportAttachmentOption("asset-1", "证据", "screen.png", 4);
        viewModel.ExportAttachmentOptions.Add(option);
        Assert.Empty(viewModel.SelectedExportAttachmentIds);
        option.IsIncluded = true;
        Assert.Equal(["asset-1"], viewModel.SelectedExportAttachmentIds);
        viewModel.ExportPreview = "已冻结预览";
        viewModel.CanConfirmExport = true;

        viewModel.ExportScope = "当前页选中交易";

        Assert.False(viewModel.CanConfirmExport);
        Assert.Empty(viewModel.ExportAttachmentOptions);
        Assert.Contains("默认不包含", viewModel.ExportPreview);

        viewModel.CanConfirmExport = true;
        viewModel.ResetAccountState("账户切换");
        Assert.False(viewModel.CanConfirmExport);
    }

    [Fact]
    public async Task ReviewAutoSave_WaitsForInjectedScheduler()
    {
        var scheduler = new ManualAsyncScheduler();
        var viewModel = new ReviewWorkspaceViewModel(scheduler)
        {
            SelectedPositionId = 42,
        };
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        viewModel.AutoSaveReviewAsync = () =>
        {
            Interlocked.Increment(ref saveCount);
            saved.TrySetResult();
            return Task.CompletedTask;
        };

        viewModel.EntryReason = "等待可控调度";
        var scheduled = await scheduler.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromMilliseconds(900), scheduled.Delay);
        Assert.Equal(0, Volatile.Read(ref saveCount));

        scheduled.Release();
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref saveCount));
    }

    [Fact]
    public async Task AccountSwitch_CancelsOldSessionBeforePublishingNextGeneration()
    {
        using var sessions = new AccountSessionCoordinator();
        var first = await sessions.SwitchAsync("account-a");
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.CancellationToken.Register(() => cancellationObserved.TrySetResult());

        var second = await sessions.SwitchAsync("account-b");

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.True(sessions.IsCurrent("account-b", second.Generation));
        Assert.False(sessions.IsCurrent("account-a", first.Generation));
    }

    [Fact]
    public void SaveReceipt_RejectsOtherAccountAndOlderContent()
    {
        var editorId = Guid.NewGuid();
        var identityA = EditIdentity.ForTrade(new TradeKey("account-a", 42), 7, editorId);
        var firstSnapshot = new EditSnapshot<string>(identityA, 1, "first");
        var firstReceipt = EditSaveReceipt<string>.Saved(firstSnapshot, "stored-first");
        var identityB = EditIdentity.ForTrade(new TradeKey("account-b", 42), 8, Guid.NewGuid());

        Assert.True(firstReceipt.CanAcknowledge(identityA, 1));
        Assert.False(firstReceipt.CanAcknowledge(identityA, 2));
        Assert.False(firstReceipt.CanAcknowledge(identityB, 1));
    }

    [Fact]
    public void AccountEditors_KeepDraftOnlyWithItsTradeKeyAndRejectLateReceipt()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        var detailA = Detail("imported:account-a", 42);
        viewModel.ApplyDetail(detailA, sessionGeneration: 1);
        viewModel.EntryReason = "A 账户未提交草稿";
        var submissionA = Assert.IsType<TradeReviewEditSubmission>(
            viewModel.CaptureTradeReviewEdit(new TradeReviewBasis("source-a", "rule-a")));

        viewModel.ResetAccountState("当前连接模式不支持账户复盘");
        Assert.Null(viewModel.TradeEditorKey);
        Assert.Null(viewModel.SelectedPositionId);
        Assert.Empty(viewModel.EntryReason);
        Assert.Empty(viewModel.Trades);

        var detailB = Detail("LiveBroker|1001", 42);
        viewModel.ApplyDetail(detailB, sessionGeneration: 2);

        Assert.Equal(string.Empty, viewModel.EntryReason);
        Assert.False(viewModel.HasUnsavedReviewChanges);
        Assert.Equal(new TradeKey("LiveBroker|1001", 42), viewModel.TradeEditorKey);

        var storedA = ReviewDocument(
            submissionA.Snapshot.Content.TradeKey,
            revision: 1,
            "A 已保存版本",
            entryReason: "A 账户未提交草稿");
        var lateReceipt = new EditSaveReceipt<TradeReviewDocument>(
            submissionA.Snapshot.Identity,
            submissionA.Snapshot.ContentSequence,
            EditSaveStatus.Saved,
            storedA,
            string.Empty);
        Assert.False(viewModel.ApplyTradeReviewSaveReceipt(lateReceipt));
        Assert.Equal(string.Empty, viewModel.EntryReason);
        Assert.Equal(new TradeKey("LiveBroker|1001", 42), viewModel.TradeEditorKey);

        viewModel.ResetAccountState("switching");
        viewModel.ApplyDetail(Detail("imported:account-a", 42, storedA), sessionGeneration: 3);
        Assert.Equal("A 账户未提交草稿", viewModel.EntryReason);
        Assert.False(viewModel.HasUnsavedReviewChanges);
        Assert.Equal(new TradeKey("imported:account-a", 42), viewModel.TradeEditorKey);
    }

    [Fact]
    public void SaveReceipt_DoesNotClearEditsMadeWhileSaveWasAwaiting()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        viewModel.ApplyDetail(Detail("account-a", 9), sessionGeneration: 4);
        viewModel.Summary = "提交中的版本";
        var submitted = Assert.IsType<TradeReviewEditSubmission>(
            viewModel.CaptureTradeReviewEdit(new TradeReviewBasis("source", "rule")));
        viewModel.Summary = "提交后继续输入的新版本";
        var receipt = new EditSaveReceipt<TradeReviewDocument>(
            submitted.Snapshot.Identity,
            submitted.Snapshot.ContentSequence,
            EditSaveStatus.Saved,
            ReviewDocument(submitted.Snapshot.Content.TradeKey, 1, "提交中的版本"),
            string.Empty);

        Assert.False(viewModel.ApplyTradeReviewSaveReceipt(receipt));
        Assert.True(viewModel.HasUnsavedReviewChanges);
        Assert.Equal("提交后继续输入的新版本", viewModel.Summary);
        viewModel.ResetAccountState("cleanup", preserveTradeDraft: false);
    }

    [Fact]
    public void DailyEditor_RefreshAndOldReceiptPreserveNewestDraft()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        var date = new DateOnly(2026, 9, 10);
        viewModel.ApplyDetail(Detail("account-a", 11), sessionGeneration: 1);
        viewModel.ApplyDaily(date, null, null, "source-1");
        viewModel.PreMarketPlan = "提交中的计划";
        var submittedJournal = DailyJournal("account-a", date, "提交中的计划", revision: 0);
        var submitted = Assert.IsType<EditSnapshot<DailyJournal>>(
            viewModel.CaptureWorkspaceEdit(EditEntityKind.DailyJournal, submittedJournal));

        viewModel.PreMarketPlan = "等待期间继续输入的计划";
        var receipt = new EditSaveReceipt<DailyJournal>(
            submitted.Identity,
            submitted.ContentSequence,
            EditSaveStatus.Saved,
            submittedJournal with { Revision = 1 },
            string.Empty);

        Assert.False(viewModel.ApplyWorkspaceSaveReceipt(EditEntityKind.DailyJournal, receipt));
        Assert.True(viewModel.IsEditorDirty(EditEntityKind.DailyJournal));

        viewModel.ApplyDaily(
            date,
            DailyJournal("account-a", date, "服务器中的旧计划", revision: 1),
            null,
            "source-1");
        Assert.Equal("等待期间继续输入的计划", viewModel.PreMarketPlan);

        var retry = Assert.IsType<EditSnapshot<DailyJournal>>(
            viewModel.CaptureWorkspaceEdit(
                EditEntityKind.DailyJournal,
                DailyJournal("account-a", date, viewModel.PreMarketPlan, revision: 1)));
        var conflict = new EditSaveReceipt<DailyJournal>(
            retry.Identity,
            retry.ContentSequence,
            EditSaveStatus.Conflict,
            null,
            "服务器修订已变化");
        Assert.False(viewModel.ApplyWorkspaceSaveReceipt(EditEntityKind.DailyJournal, conflict));
        Assert.True(viewModel.HasUnsavedWorkspaceChanges);
        Assert.Contains("服务器修订已变化", viewModel.EditorSaveStatus);
        Assert.Equal("等待期间继续输入的计划",
            Assert.Single(viewModel.ExportPendingDrafts(), item => item.Kind == EditEntityKind.DailyJournal)
                .Fields[nameof(ReviewWorkspaceViewModel.PreMarketPlan)]);
        viewModel.ResetAccountState("cleanup", preserveTradeDraft: false);
    }

    [Fact]
    public void GoalDrafts_AreIsolatedAcrossAccountsAndRestoredByEntityKind()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        viewModel.ApplyDetail(Detail("account-a", 1), sessionGeneration: 1);
        viewModel.NewGoalCommand.Execute(null);
        viewModel.GoalName = "A 的纪律目标";
        viewModel.GoalMeasurement = "每次机会都检查";

        viewModel.ResetAccountState("switch");
        viewModel.ApplyDetail(Detail("account-b", 1), sessionGeneration: 2);
        viewModel.NewGoalCommand.Execute(null);
        Assert.Empty(viewModel.GoalName);
        Assert.False(viewModel.IsEditorDirty(EditEntityKind.ImprovementGoal));

        viewModel.ResetAccountState("switch");
        viewModel.ApplyDetail(Detail("account-a", 1), sessionGeneration: 3);
        viewModel.NewGoalCommand.Execute(null);
        Assert.Equal("A 的纪律目标", viewModel.GoalName);
        Assert.Equal("每次机会都检查", viewModel.GoalMeasurement);
        Assert.True(viewModel.IsEditorDirty(EditEntityKind.ImprovementGoal));
        viewModel.ResetAccountState("cleanup", preserveTradeDraft: false);
    }

    [Fact]
    public async Task WorkspaceEditor_AutoSavesAfterInjectedDelay()
    {
        var scheduler = new ManualAsyncScheduler();
        var viewModel = new ReviewWorkspaceViewModel(scheduler);
        viewModel.ApplyDetail(Detail("account-a", 1), sessionGeneration: 1);
        viewModel.ApplyDaily(new DateOnly(2026, 9, 10), null, null, "source-1");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.AutoSaveWorkspaceAsync = () =>
        {
            saved.TrySetResult();
            return Task.CompletedTask;
        };

        viewModel.DailyNextAction = "明天只检查一个动作";
        var delay = await scheduler.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromMilliseconds(900), delay.Delay);
        Assert.False(saved.Task.IsCompleted);
        delay.Release();
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ResetAccountState("cleanup", preserveTradeDraft: false);
    }

    [Fact]
    public void ShutdownEditGuard_FreezesInputAndExportsOnlyPendingDrafts()
    {
        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler());
        var date = new DateOnly(2026, 9, 10);
        viewModel.ApplyDetail(Detail("account-a", 1), sessionGeneration: 1);
        viewModel.ApplyDaily(date, null, null, "source-1");
        viewModel.DailyNextAction = "退出前内容";

        viewModel.BeginShutdownEdits();
        viewModel.DailyNextAction = "退出后不应接受";

        Assert.Equal("退出前内容", viewModel.DailyNextAction);
        var draft = Assert.Single(viewModel.ExportPendingDrafts(), item => item.Kind == EditEntityKind.DailyJournal);
        Assert.Equal("退出前内容", draft.Fields[nameof(ReviewWorkspaceViewModel.DailyNextAction)]);

        viewModel.ResumeEdits();
        viewModel.DailyNextAction = "返回程序后可继续";
        Assert.Equal("返回程序后可继续", viewModel.DailyNextAction);
        viewModel.ResetAccountState("cleanup", preserveTradeDraft: false);
    }

    [Fact]
    public void DetailRequestContext_AppliesOnlyLatestMatchingAccountGenerationAndTrade()
    {
        var keyA = new TradeKey("account-a", 42);
        var keyB = new TradeKey("account-b", 42);
        var firstA = new TradeDetailRequestContext("request-a1", 1, keyA);
        var repeatedA = new TradeDetailRequestContext("request-a-repeat", 1, keyA);
        var requestB = new TradeDetailRequestContext("request-b", 2, keyB);
        var latestA = new TradeDetailRequestContext("request-a2", 3, keyA);

        Assert.False(firstA.CanApply(latestA.RequestId, "account-a", 3, keyA));
        Assert.False(firstA.CanApply(repeatedA.RequestId, "account-a", 1, keyA));
        Assert.True(repeatedA.CanApply(repeatedA.RequestId, "account-a", 1, keyA));
        Assert.False(requestB.CanApply(latestA.RequestId, "account-a", 3, keyB));
        Assert.False(latestA.CanApply(latestA.RequestId, "account-a", 3, keyB));
        Assert.True(latestA.CanApply(latestA.RequestId, "account-a", 3, keyA));
    }

    [Fact]
    public async Task Maintenance_WaitsForActiveOperationAndBlocksNewOperation()
    {
        using var maintenance = new MaintenanceCoordinator();
        var active = await maintenance.EnterOperationAsync(MaintenanceOperationKind.Write);
        var maintenanceTask = maintenance.EnterMaintenanceAsync().AsTask();
        Assert.True(SpinWait.SpinUntil(() => maintenance.IsMaintenancePending, TimeSpan.FromSeconds(2)));
        Assert.False(maintenanceTask.IsCompleted);

        var blockedTask = maintenance.EnterOperationAsync(MaintenanceOperationKind.Read).AsTask();
        Assert.False(blockedTask.IsCompleted);

        await active.DisposeAsync();
        var maintenanceLease = await maintenanceTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, maintenance.ActiveOperationCount);
        Assert.False(blockedTask.IsCompleted);

        await maintenanceLease.DisposeAsync();
        var admitted = await blockedTask.WaitAsync(TimeSpan.FromSeconds(2));
        await admitted.DisposeAsync();
    }

    [Fact]
    public async Task Maintenance_DrainsConcurrentQueryReplayAndWriteBeforeAdmittingRestore()
    {
        using var maintenance = new MaintenanceCoordinator();
        var query = await maintenance.EnterOperationAsync(MaintenanceOperationKind.Read);
        var replay = await maintenance.EnterOperationAsync(MaintenanceOperationKind.File);
        var write = await maintenance.EnterOperationAsync(MaintenanceOperationKind.Write);
        var restore = maintenance.EnterMaintenanceAsync().AsTask();
        Assert.True(SpinWait.SpinUntil(() => maintenance.IsMaintenancePending, TimeSpan.FromSeconds(2)));
        var lateRead = maintenance.EnterOperationAsync(MaintenanceOperationKind.Read).AsTask();

        await query.DisposeAsync();
        await replay.DisposeAsync();
        Assert.False(restore.IsCompleted);
        Assert.False(lateRead.IsCompleted);
        await write.DisposeAsync();
        var lease = await restore.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, maintenance.ActiveOperationCount);
        Assert.False(lateRead.IsCompleted);
        await lease.DisposeAsync();
        await (await lateRead.WaitAsync(TimeSpan.FromSeconds(2))).DisposeAsync();
    }

    [Fact]
    public async Task Composition_AcceptsRepositoryAndFileFailureDoublesWithoutStartingMt5()
    {
        var repository = DispatchProxy.Create<IReviewWorkspaceRepository, ThrowingProxy>();
        var packageWriter = DispatchProxy.Create<IReviewPackageWriter, ThrowingProxy>();
        var attachmentStore = DispatchProxy.Create<IReviewAttachmentStore, ThrowingProxy>();
        var backupService = DispatchProxy.Create<IReviewBackupService, ThrowingProxy>();
        using var sessions = new AccountSessionCoordinator();
        using var maintenance = new MaintenanceCoordinator();
        var database = new AppDatabase(Path.Combine(Path.GetTempPath(), $"tradepet-wp02-{Guid.NewGuid():N}.db"));
        var dependencies = new TradePetRuntimeDependencies(
            database,
            repository,
            packageWriter,
            attachmentStore,
            backupService,
            TimeProvider.System,
            new ManualAsyncScheduler(),
            sessions,
            maintenance);

        await using var runtime = new TradePetRuntime(
            new TradePet.App.ViewModels.MainViewModel(dependencies.Scheduler, dependencies.TimeProvider),
            dependencies);

        await Assert.ThrowsAsync<IOException>(() =>
            dependencies.ReviewRepository.LoadReviewDataVersionAsync("account-a"));
        await Assert.ThrowsAsync<IOException>(() =>
            dependencies.ReviewPackageWriter.WriteAsync(new ReviewExportPackage("x.zip", "v1", [])));
        await Assert.ThrowsAsync<IOException>(() =>
            dependencies.ReviewBackupService.ValidateAsync("missing.zip"));
    }

    [Fact]
    public void ReviewWorkspace_UsesInjectedClockForInitialServerDateEditor()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2031, 4, 5, 12, 0, 0, TimeSpan.Zero));

        var viewModel = new ReviewWorkspaceViewModel(new ManualAsyncScheduler(), clock);

        Assert.Equal("2031-04-05", viewModel.DailyDate);
    }

    private sealed class ManualAsyncScheduler : IAsyncScheduler
    {
        private readonly ConcurrentQueue<ScheduledDelay> _scheduled = new();
        private readonly SemaphoreSlim _available = new(0);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            var scheduled = new ScheduledDelay(delay, cancellationToken);
            _scheduled.Enqueue(scheduled);
            _available.Release();
            return scheduled.Task;
        }

        public async Task<ScheduledDelay> NextAsync(CancellationToken cancellationToken = default)
        {
            await _available.WaitAsync(cancellationToken);
            return _scheduled.TryDequeue(out var scheduled)
                ? scheduled
                : throw new InvalidOperationException("调度信号与队列不一致。");
        }
    }

    private sealed class ScheduledDelay
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScheduledDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        }

        public TimeSpan Delay { get; }
        public Task Task => _completion.Task;
        public void Release() => _completion.TrySetResult();
    }

    private class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new IOException($"Injected failure: {targetMethod?.Name}");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static TradeDetailData Detail(
        string accountKey,
        long positionId,
        TradeReviewDocument? document = null)
    {
        var now = new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero);
        var trade = new TradeRecord(
            accountKey,
            positionId,
            "XAUUSD.s",
            TradeSide.Buy,
            now.AddMinutes(-5),
            now,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 10),
            3500m,
            3501m,
            0.01m,
            0.01m,
            0m,
            1m,
            true);
        return new TradeDetailData(
            trade,
            [],
            null,
            document,
            null,
            [],
            null,
            null,
            [],
            [],
            [],
            null,
            new ReviewDataVersion(accountKey, 1, 1, 1, "rule", "time", now));
    }

    private static TradeReviewDocument ReviewDocument(
        TradeKey key,
        int revision,
        string summary,
        string entryReason = "")
    {
        var now = new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero);
        return new TradeReviewDocument(
            key,
            ReviewCompletionStatus.Draft,
            entryReason,
            string.Empty,
            string.Empty,
            string.Empty,
            "next",
            summary,
            string.Empty,
            string.Empty,
            revision,
            "source",
            "rule",
            null,
            null,
            now,
            now);
    }

    private static DailyJournal DailyJournal(
        string accountKey,
        DateOnly date,
        string preMarketPlan,
        int revision)
    {
        var now = new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero);
        return new DailyJournal(
            accountKey,
            date,
            preMarketPlan,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "next",
            ReviewCompletionStatus.Draft,
            revision,
            "source-1",
            now,
            now);
    }
}
