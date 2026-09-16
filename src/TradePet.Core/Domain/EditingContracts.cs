namespace TradePet.Core.Domain;

public enum EditEntityKind
{
    TradeReview,
    DailyJournal,
    PeriodReview,
    RuleAssessment,
    Opportunity,
    ImprovementGoal,
    Annotation,
}

public sealed record EditIdentity
{
    public EditIdentity(
        string accountKey,
        EditEntityKind entityKind,
        string entityId,
        long sessionGeneration,
        Guid editorInstanceId)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new ArgumentException("编辑身份必须包含账户。", nameof(accountKey));
        }
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("编辑身份必须包含实体。", nameof(entityId));
        }
        if (sessionGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        }
        if (editorInstanceId == Guid.Empty)
        {
            throw new ArgumentException("编辑器实例不能为空。", nameof(editorInstanceId));
        }

        AccountKey = accountKey.Trim();
        EntityKind = entityKind;
        EntityId = entityId.Trim();
        SessionGeneration = sessionGeneration;
        EditorInstanceId = editorInstanceId;
    }

    public string AccountKey { get; }
    public EditEntityKind EntityKind { get; }
    public string EntityId { get; }
    public long SessionGeneration { get; }
    public Guid EditorInstanceId { get; }

    public static EditIdentity ForTrade(TradeKey key, long sessionGeneration, Guid editorInstanceId) =>
        new(key.AccountKey, EditEntityKind.TradeReview, key.PositionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sessionGeneration, editorInstanceId);

    public bool IsForTrade(TradeKey key) =>
        EntityKind == EditEntityKind.TradeReview &&
        string.Equals(AccountKey, key.AccountKey, StringComparison.Ordinal) &&
        string.Equals(EntityId,
            key.PositionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
}

public sealed record EditSnapshot<T>
{
    public EditSnapshot(EditIdentity identity, long contentSequence, T content)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (contentSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contentSequence));
        }

        ContentSequence = contentSequence;
        Content = content;
    }

    public EditIdentity Identity { get; }
    public long ContentSequence { get; }
    public T Content { get; }
}

public enum EditSaveStatus
{
    Saved,
    ValidationFailed,
    Conflict,
    NotFound,
    StorageFailed,
    Cancelled,
}

public enum EditPersistenceState
{
    Clean,
    Dirty,
    Saving,
    Conflict,
    Failed,
}

public sealed record EditState(
    EditIdentity Identity,
    long ContentSequence,
    EditPersistenceState PersistenceState,
    string Message = "")
{
    public bool IsDirty => PersistenceState is not EditPersistenceState.Clean;

    public static EditState Clean(EditIdentity identity) =>
        new(identity ?? throw new ArgumentNullException(nameof(identity)), 1, EditPersistenceState.Clean);

    public EditState Changed() => this with
    {
        ContentSequence = Math.Max(1, ContentSequence + 1),
        PersistenceState = EditPersistenceState.Dirty,
        Message = string.Empty,
    };

    public EditState Saving() => this with
    {
        PersistenceState = EditPersistenceState.Saving,
        Message = string.Empty,
    };

    public EditState Apply<T>(EditSaveReceipt<T> receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Identity != Identity)
        {
            return this;
        }
        if (!receipt.IsSaved)
        {
            return this with
            {
                PersistenceState = receipt.Status == EditSaveStatus.Conflict
                    ? EditPersistenceState.Conflict
                    : EditPersistenceState.Failed,
                Message = receipt.Message,
            };
        }
        return receipt.ContentSequence == ContentSequence
            ? this with { PersistenceState = EditPersistenceState.Clean, Message = string.Empty }
            : this with { PersistenceState = EditPersistenceState.Dirty, Message = "较早版本已保存，当前仍有新修改。" };
    }
}

public sealed record EditSaveReceipt<T>
{
    public EditSaveReceipt(
        EditIdentity identity,
        long contentSequence,
        EditSaveStatus status,
        T? value,
        string message)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (contentSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contentSequence));
        }

        ContentSequence = contentSequence;
        Status = status;
        Value = value;
        Message = message ?? string.Empty;
    }

    public EditIdentity Identity { get; }
    public long ContentSequence { get; }
    public EditSaveStatus Status { get; }
    public T? Value { get; }
    public string Message { get; }
    public bool IsSaved => Status == EditSaveStatus.Saved;

    public bool CanAcknowledge(EditIdentity currentIdentity, long currentContentSequence) =>
        IsSaved &&
        currentIdentity is not null &&
        Identity == currentIdentity &&
        ContentSequence == currentContentSequence;

    public static EditSaveReceipt<T> Saved(EditSnapshot<T> snapshot, T value) =>
        new(snapshot.Identity, snapshot.ContentSequence, EditSaveStatus.Saved, value, string.Empty);

    public static EditSaveReceipt<T> Failed(
        EditSnapshot<T> snapshot,
        EditSaveStatus status,
        string message)
    {
        if (status == EditSaveStatus.Saved)
        {
            throw new ArgumentException("失败回执不能使用 Saved 状态。", nameof(status));
        }

        return new EditSaveReceipt<T>(snapshot.Identity, snapshot.ContentSequence, status, default, message);
    }
}
