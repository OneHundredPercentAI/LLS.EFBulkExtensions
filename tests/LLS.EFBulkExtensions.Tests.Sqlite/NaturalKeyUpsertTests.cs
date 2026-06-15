using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Tests.Sqlite;

/// <summary>
/// Upsert correlacionando por chave natural (MatchProperties) em vez da PK.
/// A entidade tem PK identity (Id) e um índice ÚNICO na coluna natural (Sku).
/// </summary>
public class NaturalKeyUpsertTests
{
    private static async Task<(ProductContext Ctx, SqliteConnection Conn)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProductContext>().UseSqlite(connection).Options;
        var ctx = new ProductContext(options);
        await ctx.Database.EnsureCreatedAsync();
        return (ctx, connection);
    }

    [Fact]
    public async Task BulkInsertOrUpdate_ByNaturalKey_MatchesOnUniqueIndex()
    {
        var (ctx, conn) = await CreateAsync();
        await using var _ = ctx;
        await using var __ = conn;

        var seeded = new List<Product>
        {
            new() { Sku = "A", Name = "Apple", Stock = 1 },
            new() { Sku = "B", Name = "Banana", Stock = 2 },
        };
        await ctx.AddRangeAsync(seeded);
        await ctx.SaveChangesAsync();
        var idA = seeded[0].Id;
        var idB = seeded[1].Id;
        ctx.ChangeTracker.Clear();

        // Correlaciona por Sku (sem informar Id): "A" existe → atualiza; "C" é novo → insere.
        var upsert = new List<Product>
        {
            new() { Sku = "A", Name = "Apricot", Stock = 10 },
            new() { Sku = "C", Name = "Cherry", Stock = 3 },
        };
        await ctx.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions
        {
            MatchProperties = new[] { nameof(Product.Sku) }
        });

        ctx.ChangeTracker.Clear();
        var all = await ctx.Products.OrderBy(p => p.Sku).ToListAsync();
        Assert.Equal(3, all.Count); // A, B, C (sem duplicar "A")

        var a = all.Single(p => p.Sku == "A");
        Assert.Equal("Apricot", a.Name); // atualizado pela correlação por Sku
        Assert.Equal(10, a.Stock);
        Assert.Equal(idA, a.Id);          // PK preservada: casou por Sku, não criou linha nova

        var b = all.Single(p => p.Sku == "B");
        Assert.Equal(idB, b.Id);          // intacto

        var c = all.Single(p => p.Sku == "C");
        Assert.True(c.Id > 0 && c.Id != idA && c.Id != idB); // novo Id gerado pelo banco
    }

    [Fact]
    public async Task BulkInsertOrUpdate_ByNonUniqueProperty_Throws()
    {
        var (ctx, conn) = await CreateAsync();
        await using var _ = ctx;
        await using var __ = conn;

        // Name não tem índice único → o mecanismo nativo não suporta; deve lançar erro claro.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.BulkInsertOrUpdateAsync(
                new List<Product> { new() { Sku = "X", Name = "N", Stock = 1 } },
                new BulkInsertOrUpdateOptions { MatchProperties = new[] { nameof(Product.Name) } }));
        Assert.Contains("ÚNICO", ex.Message);
    }
}

public class ProductContext(DbContextOptions<ProductContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedOnAdd();
            b.Property(p => p.Sku).HasMaxLength(50).IsRequired();
            b.Property(p => p.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(p => p.Sku).IsUnique(); // chave natural
        });
    }
}

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Stock { get; set; }
}
