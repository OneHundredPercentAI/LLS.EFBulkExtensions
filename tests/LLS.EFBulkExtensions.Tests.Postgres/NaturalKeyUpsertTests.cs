using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Tests.Postgres;

/// <summary>
/// Upsert por chave natural (MatchProperties) contra PostgreSQL real: exercita
/// <c>INSERT ... ON CONFLICT (sku) DO UPDATE</c>, que exige um índice ÚNICO em sku.
/// </summary>
public class NaturalKeyUpsertTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=127.0.0.1;Database=BulkDetTestDb;Username=postgres;Password=abc1234$";

    private static DbContextOptions<ProductContext> Opts() =>
        new DbContextOptionsBuilder<ProductContext>().UseNpgsql(ConnectionString).Options;

    public async Task InitializeAsync()
    {
        using var ctx = new ProductContext(Opts());
        // EnsureCreated não cria tabela nova em banco já existente; recriamos via DDL.
        await ctx.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS products_nk;");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE products_nk (id serial PRIMARY KEY, sku varchar(50) NOT NULL UNIQUE, name varchar(100) NOT NULL, stock integer NOT NULL);");
    }

    public async Task DisposeAsync()
    {
        using var ctx = new ProductContext(Opts());
        await ctx.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS products_nk;");
    }

    [Fact]
    public async Task BulkInsertOrUpdate_ByNaturalKey_MatchesOnUniqueIndex()
    {
        int idA, idB;
        using (var seed = new ProductContext(Opts()))
        {
            var seeded = new List<Product>
            {
                new() { Sku = "A", Name = "Apple", Stock = 1 },
                new() { Sku = "B", Name = "Banana", Stock = 2 },
            };
            await seed.AddRangeAsync(seeded);
            await seed.SaveChangesAsync();
            idA = seeded[0].Id;
            idB = seeded[1].Id;
        }

        var upsert = new List<Product>
        {
            new() { Sku = "A", Name = "Apricot", Stock = 10 }, // existe → atualiza
            new() { Sku = "C", Name = "Cherry", Stock = 3 },   // novo → insere
        };
        using (var ctx = new ProductContext(Opts()))
            await ctx.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions
            {
                MatchProperties = new[] { nameof(Product.Sku) }
            });

        using var verify = new ProductContext(Opts());
        var all = await verify.Products.OrderBy(p => p.Sku).ToListAsync();
        Assert.Equal(3, all.Count);

        var a = all.Single(p => p.Sku == "A");
        Assert.Equal("Apricot", a.Name);
        Assert.Equal(10, a.Stock);
        Assert.Equal(idA, a.Id); // PK preservada (casou por sku)

        var c = all.Single(p => p.Sku == "C");
        Assert.True(c.Id > 0 && c.Id != idA && c.Id != idB);
    }
}

public class ProductContext(DbContextOptions<ProductContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products_nk");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(50).IsRequired();
            b.Property(p => p.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(p => p.Stock).HasColumnName("stock");
            b.HasIndex(p => p.Sku).IsUnique();
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
