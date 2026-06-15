using System.Collections;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace LLS.EFBulkExtensions.Benchmark;

public enum DbProvider { SqlServer, Postgres, MySql, MariaDb, Sqlite }

public sealed record DbTarget(DbProvider Provider, string Name, string ConnectionString);

public sealed record BenchResult(
    string Provider, int Rows, int Columns,
    long GenMs, long InsertMs, long RowsInDb, bool? IdsReturned, string? Error);

/// <summary>Connection strings padrão (alinhadas aos servidores locais de teste). Editáveis no menu.</summary>
public static class Defaults
{
    public static List<DbTarget> Targets() => new()
    {
        new(DbProvider.SqlServer, "SQL Server", "Server=127.0.0.1;Database=BulkBenchDb;User Id=sa;Password=abc1234$;TrustServerCertificate=True;Encrypt=False"),
        new(DbProvider.Postgres,  "PostgreSQL", "Host=127.0.0.1;Port=5432;Database=bulkbenchdb;Username=postgres;Password=abc1234$"),
        new(DbProvider.MySql,     "MySQL",      "Server=127.0.0.1;Port=3306;Database=BulkBenchDb;User ID=root;Password=abc1234$;AllowLoadLocalInfile=true"),
        new(DbProvider.MariaDb,   "MariaDB",    "Server=127.0.0.1;Port=3307;Database=BulkBenchDb;User ID=root;Password=abc1234$;AllowLoadLocalInfile=true"),
        new(DbProvider.Sqlite,    "SQLite",     "Data Source=bulk_bench.db"),
    };
}

/// <summary>
/// Garante que cada combinação (provider + layout do tipo dinâmico) tenha seu próprio modelo em cache.
/// Sem isso, o EF reusaria o modelo do primeiro provider/coluna para os demais (uma única classe de contexto).
/// </summary>
public sealed class BenchModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var c = (BenchContext)context;
        return (typeof(BenchContext), c.Provider, c.EntityType, designTime);
    }
}

public sealed class BenchContext : DbContext
{
    public DbProvider Provider { get; }
    public Type EntityType { get; }

    public BenchContext(DbContextOptions options, DbProvider provider, Type entityType) : base(options)
    {
        Provider = provider;
        EntityType = entityType;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(EntityType, b =>
        {
            b.ToTable("bench");
            b.HasKey("Id");
            b.Property("Id").ValueGeneratedOnAdd();
        });
    }
}

public static class BenchmarkRunner
{
    private static readonly MethodInfo BulkInsertGeneric =
        typeof(BulkInsertExtensions).GetMethod(nameof(BulkInsertExtensions.BulkInsertAsync))!;

    private static DbContextOptions BuildOptions(DbProvider provider, string cs)
    {
        var b = new DbContextOptionsBuilder();
        switch (provider)
        {
            case DbProvider.SqlServer: b.UseSqlServer(cs); break;
            case DbProvider.Postgres: b.UseNpgsql(cs); break;
            case DbProvider.MySql:
            case DbProvider.MariaDb: b.UseMySql(cs, ServerVersion.AutoDetect(cs)); break;
            case DbProvider.Sqlite: b.UseSqlite(cs); break;
        }
        b.ReplaceService<IModelCacheKeyFactory, BenchModelCacheKeyFactory>();
        return b.Options;
    }

    public static async Task<BenchResult> RunAsync(
        DbTarget target, Type entityType, int rows, int columns, bool returnIds, CancellationToken ct = default)
    {
        try
        {
            // 1) Geração dos dados (fora da medição do insert).
            var factory = new RowFactory(entityType);
            var swGen = Stopwatch.StartNew();
            var data = factory.Build(rows, seed: 20260614);
            swGen.Stop();

            var options = BuildOptions(target.Provider, target.ConnectionString);
            await using var ctx = new BenchContext(options, target.Provider, entityType);

            // 2) Schema limpo (recria a tabela para o nº de colunas atual).
            await ctx.Database.EnsureDeletedAsync(ct);
            await ctx.Database.EnsureCreatedAsync(ct);
            if (target.Provider is DbProvider.MySql or DbProvider.MariaDb)
            {
                // MySqlBulkCopy usa LOAD DATA LOCAL INFILE.
                await ctx.Database.ExecuteSqlRawAsync("SET GLOBAL local_infile = 1;", ct);
            }

            // 3) BulkInsert cronometrado (invocado por reflexão sobre o tipo dinâmico).
            var opts = new BulkInsertOptions { ReturnGeneratedIds = returnIds };
            var invoke = BulkInsertGeneric.MakeGenericMethod(entityType);

            var swIns = Stopwatch.StartNew();
            var task = (Task)invoke.Invoke(null, new object?[] { ctx, data, opts, ct })!;
            await task;
            swIns.Stop();

            var count = CountRows(ctx);
            bool? idsOk = returnIds && data.Count > 0
                ? factory.ReadId(data[0]!) > 0 && factory.ReadId(data[data.Count - 1]!) > 0
                : (bool?)null;

            return new BenchResult(target.Name, rows, columns, swGen.ElapsedMilliseconds,
                swIns.ElapsedMilliseconds, count, idsOk, null);
        }
        catch (Exception ex)
        {
            var msg = (ex is TargetInvocationException tie ? tie.InnerException : ex)?.Message ?? ex.Message;
            return new BenchResult(target.Name, rows, columns, 0, 0, 0, null, msg);
        }
    }

    private static long CountRows(DbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        var mustClose = conn.State != ConnectionState.Open;
        if (mustClose) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM bench";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        finally
        {
            if (mustClose) conn.Close();
        }
    }
}
