using TradePet.Core.Domain;

namespace TradePet.Core.Session;

public sealed record HistorySyncPlan(
    string AccountKey,
    int EarliestYear,
    int CurrentYear,
    IReadOnlyList<int> RequestedYears);

public sealed record HistorySyncBatchResult(
    bool RequiresRetry,
    int PendingYearCount);

public sealed class HistorySyncTracker
{
    private readonly object _sync = new();
    private readonly HashSet<int> _pendingYears = [];
    private readonly HashSet<int> _yearsWithPersistenceFailure = [];
    private string? _requestedScope;

    public bool ShouldRequest(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        lock (_sync)
        {
            return !string.Equals(_requestedScope, accountKey, StringComparison.Ordinal);
        }
    }

    public HistorySyncPlan? BeginRequest(
        string accountKey,
        int currentYear,
        IReadOnlyCollection<HistorySyncState> persistedStates,
        int lookbackYears = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentNullException.ThrowIfNull(persistedStates);
        if (currentYear < 1970)
        {
            throw new ArgumentOutOfRangeException(nameof(currentYear));
        }
        if (lookbackYears < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookbackYears));
        }

        lock (_sync)
        {
            if (string.Equals(_requestedScope, accountKey, StringComparison.Ordinal))
            {
                return null;
            }

            _requestedScope = accountKey;
            var completedYears = persistedStates
                .Where(state =>
                    state.AccountKey == accountKey &&
                    state.IsComplete &&
                    state.RangeYear != currentYear)
                .Select(state => state.RangeYear)
                .ToHashSet();
            var earliestYear = Math.Max(1970, currentYear - lookbackYears);
            var requestedYears = Enumerable
                .Range(earliestYear, currentYear - earliestYear + 1)
                .OrderDescending()
                .Where(year => !completedYears.Contains(year))
                .ToArray();

            _pendingYears.Clear();
            foreach (var year in requestedYears)
            {
                _pendingYears.Add(year);
            }

            return new HistorySyncPlan(accountKey, earliestYear, currentYear, requestedYears);
        }
    }

    public bool CanMarkComplete(int rangeYear, bool sourceComplete)
    {
        lock (_sync)
        {
            return sourceComplete && !_yearsWithPersistenceFailure.Contains(rangeYear);
        }
    }

    public HistorySyncBatchResult RecordBatch(
        int rangeYear,
        bool sourceComplete,
        bool batchPersisted)
    {
        lock (_sync)
        {
            if (!batchPersisted)
            {
                _yearsWithPersistenceFailure.Add(rangeYear);
            }

            var requiresRetry = sourceComplete && _yearsWithPersistenceFailure.Contains(rangeYear);
            if (sourceComplete && !requiresRetry)
            {
                _pendingYears.Remove(rangeYear);
            }
            else if (requiresRetry)
            {
                _yearsWithPersistenceFailure.Remove(rangeYear);
                _requestedScope = null;
            }

            return new HistorySyncBatchResult(requiresRetry, _pendingYears.Count);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _requestedScope = null;
            _pendingYears.Clear();
            _yearsWithPersistenceFailure.Clear();
        }
    }
}
