namespace LLS.EFBulkExtensions.Options;

public sealed class BulkReadOptions
{
    /// <summary>
    /// Tamanho do lote de chaves por consulta. Cada lote vira um único <c>Contains</c> traduzido
    /// pelo EF (no PostgreSQL <c>= ANY(@array)</c>, no SQL Server <c>OPENJSON</c>, em
    /// SQLite/MySQL um <c>IN</c> parametrizado). Evita um <c>IN</c> único gigante.
    /// </summary>
    public int BatchSize { get; init; } = 20_000;
}
