namespace LLS.EFBulkExtensions.Options;

public sealed class BulkInsertOptions
{
    public bool ReturnGeneratedIds { get; init; } = false;
    public int BatchSize { get; init; } = 1000;
    public int TimeoutSeconds { get; init; } = 30;
    public bool PreserveIdentity { get; init; } = false;
    public bool UseInternalTransaction { get; init; } = true;
    public bool KeepNulls { get; init; } = false;
    public bool UseAppLock { get; init; } = false;
}
