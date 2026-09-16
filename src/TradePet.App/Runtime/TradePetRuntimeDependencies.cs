using TradePet.Application.Review;
using TradePet.Application.Runtime;
using TradePet.Infrastructure.Persistence;

namespace TradePet.App.Runtime;

public sealed record TradePetRuntimeDependencies(
    AppDatabase Database,
    IReviewWorkspaceRepository ReviewRepository,
    IReviewPackageWriter ReviewPackageWriter,
    IReviewAttachmentStore ReviewAttachmentStore,
    IReviewBackupService ReviewBackupService,
    TimeProvider TimeProvider,
    IAsyncScheduler Scheduler,
    IAccountSessionCoordinator AccountSessions,
    IMaintenanceCoordinator Maintenance)
{
    public static TradePetRuntimeDependencies CreateDefault()
    {
        var timeProvider = TimeProvider.System;
        var database = new AppDatabase(TradePetPaths.GetDatabasePath());
        return new TradePetRuntimeDependencies(
            database,
            database,
            new ReviewPackageWriter(),
            new ReviewAttachmentStore(database),
            new ReviewBackupService(database, timeProvider: timeProvider),
            timeProvider,
            new SystemAsyncScheduler(timeProvider),
            new AccountSessionCoordinator(),
            new MaintenanceCoordinator());
    }
}
