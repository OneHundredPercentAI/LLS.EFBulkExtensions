namespace LLS.EFBulkExtensions.Options;

public sealed class BulkDeleteOptions
{
    public int BatchSize { get; init; } = 5000;
    public int TimeoutSeconds { get; init; } = 30;
    public bool UseInternalTransaction { get; init; } = false;
}
