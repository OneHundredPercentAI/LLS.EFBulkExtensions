namespace LLS.EFBulkExtensions.Options;

public class BulkDeleteOptions
{
    public int BatchSize { get; set; } = 5000;
    public int TimeoutSeconds { get; set; } = 30;
    public bool UseInternalTransaction { get; set; } = false;
}
