using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Tests.SqlServer;

/// <summary>
/// Testes de integração determinísticos contra um SQL Server local.
/// Usa um banco dedicado e limpa a tabela antes de cada teste para isolar resultados.
/// Foco principal: regressão do bug de transação ambiente no BulkInsert.
/// </summary>
public class SqlServerDeterministicTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=127.0.0.1;Database=BulkDetTestDb;User Id=sa;Password=abc1234$;TrustServerCertificate=True;";

    private static DbContextOptions<TestContext> Opts() =>
        new DbContextOptionsBuilder<TestContext>().UseSqlServer(ConnectionString).Options;

    public async Task InitializeAsync()
    {
        using var ctx = new TestContext(Opts());
        await ctx.Database.EnsureCreatedAsync();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM contato.people;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static List<Customer> BuildCustomers(int count)
    {
        var list = new List<Customer>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new Customer
            {
                Name = $"Person_{i}",
                Age = i % 100,
                Status = i % 2 == 0 ? PersonStatus.Active : PersonStatus.Inactive,
                CustomerCode = $"C{i:0000}",
                Contato = new ContatoPerson { Email = $"user{i}@example.com", Telefone = $"55119{i:00000000}" }
            });
        }
        return list;
    }

    [Fact]
    public async Task BulkInsert_PersistsAllRows()
    {
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(BuildCustomers(50));

        using var verify = new TestContext(Opts());
        Assert.Equal(50, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_ReturnGeneratedIds_PopulatesEntityIds()
    {
        var people = BuildCustomers(10);
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(people, new BulkInsertOptions { ReturnGeneratedIds = true });

        Assert.All(people, p => Assert.True(p.Id > 0));
        Assert.Equal(10, people.Select(p => p.Id).Distinct().Count());

        using var verify = new TestContext(Opts());
        var dbIds = await verify.People.Select(p => p.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(people.Select(p => p.Id).OrderBy(x => x), dbIds);
    }

    [Fact]
    public async Task BulkInsert_EmptyCollection_DoesNothing()
    {
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(new List<Customer>());

        using var verify = new TestContext(Opts());
        Assert.Equal(0, await verify.People.CountAsync());
    }

    // --- Regressão do bug #1: BulkInsert deve participar da transação ambiente do EF ---

    [Fact]
    public async Task BulkInsert_WithinTransaction_Rollback_PersistsNothing()
    {
        using (var ctx = new TestContext(Opts()))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await ctx.BulkInsertAsync(BuildCustomers(10), new BulkInsertOptions { UseInternalTransaction = false });
            await tx.RollbackAsync();
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(0, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_WithinTransaction_Commit_Persists()
    {
        using (var ctx = new TestContext(Opts()))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await ctx.BulkInsertAsync(BuildCustomers(10), new BulkInsertOptions { UseInternalTransaction = false });
            await tx.CommitAsync();
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(10, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_DefaultOptions_WithinTransaction_Commit_Persists()
    {
        // Default: UseInternalTransaction = true. Dentro de transação ambiente não pode
        // tentar abrir transação interna no SqlBulkCopy (mutuamente exclusivas).
        using (var ctx = new TestContext(Opts()))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await ctx.BulkInsertAsync(BuildCustomers(10));
            await tx.CommitAsync();
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(10, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_ReturnGeneratedIds_WithinTransaction_Rollback_PersistsNothing()
    {
        var people = BuildCustomers(10);
        using (var ctx = new TestContext(Opts()))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            // Antes da correção, este caminho lançava por usar comandos sem a transação ambiente.
            await ctx.BulkInsertAsync(people, new BulkInsertOptions { ReturnGeneratedIds = true, UseInternalTransaction = false });
            Assert.All(people, p => Assert.True(p.Id > 0));
            await tx.RollbackAsync();
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(0, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsertOrUpdate_UpdatesExisting_AndInsertsNew()
    {
        int[] existingIds;
        int maxSeededId;
        using (var seed = new TestContext(Opts()))
        {
            var seeded = BuildCustomers(10);
            await seed.AddRangeAsync(seeded);
            await seed.SaveChangesAsync();
            existingIds = seeded.Take(5).Select(s => s.Id).ToArray();
            maxSeededId = seeded.Max(s => s.Id);
        }

        var upsert = new List<Customer>();
        foreach (var id in existingIds)
        {
            upsert.Add(new Customer { Id = id, Name = $"Person_{id}_U", Age = 1, Status = PersonStatus.Inactive, CustomerCode = "U", Contato = new ContatoPerson { Email = "upd@x.com", Telefone = "0" } });
        }
        for (int i = 1; i <= 3; i++)
        {
            upsert.Add(new Customer { Id = maxSeededId + i, Name = $"New_{i}", Age = 1, Status = PersonStatus.Active, CustomerCode = "N", Contato = new ContatoPerson { Email = $"new{i}@x.com", Telefone = "0" } });
        }

        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertOrUpdateAsync(upsert);

        using var verify = new TestContext(Opts());
        Assert.Equal(13, await verify.People.CountAsync());
        Assert.Equal(5, await verify.People.Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive).CountAsync());
        Assert.Equal(3, await verify.People.Where(p => p.Id > maxSeededId).CountAsync());
    }

    [Fact]
    public async Task BulkUpdate_AppliesChanges()
    {
        using (var seed = new TestContext(Opts()))
        {
            await seed.AddRangeAsync(BuildCustomers(20));
            await seed.SaveChangesAsync();
        }

        using (var ctx = new TestContext(Opts()))
        {
            var toUpdate = await ctx.People.OrderBy(p => p.Id).ToListAsync();
            foreach (var p in toUpdate)
            {
                p.Name += "_U";
                p.Status = PersonStatus.Inactive;
                p.Contato.Email = "upd_" + p.Contato.Email;
            }
            await ctx.BulkUpdateAsync(toUpdate);
        }

        using var verify = new TestContext(Opts());
        var updated = await verify.People
            .Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive && p.Contato.Email.StartsWith("upd_"))
            .CountAsync();
        Assert.Equal(20, updated);
    }

    [Fact]
    public async Task BulkDelete_RemovesRows()
    {
        using (var seed = new TestContext(Opts()))
        {
            await seed.AddRangeAsync(BuildCustomers(20));
            await seed.SaveChangesAsync();
        }

        using (var ctx = new TestContext(Opts()))
        {
            var toDelete = await ctx.People.OrderBy(p => p.Id).Take(8).ToListAsync();
            await ctx.BulkDeleteAsync(toDelete);
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(12, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsertOrUpdate_UpdateColumns_OnlyUpdatesListedColumns()
    {
        int[] ids;
        using (var seed = new TestContext(Opts()))
        {
            var seeded = BuildCustomers(5);
            await seed.AddRangeAsync(seeded);
            await seed.SaveChangesAsync();
            ids = seeded.Select(s => s.Id).ToArray();
        }

        var upsert = ids.Select(id => new Customer
        {
            Id = id,
            Name = "Changed_" + id,
            Age = 999,
            Status = PersonStatus.Inactive,
            CustomerCode = "X",
            Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" }
        }).ToList();

        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions { UpdateColumns = new[] { nameof(Customer.Name) } });

        using var verify = new TestContext(Opts());
        var rows = await verify.People.OrderBy(p => p.Id).ToListAsync();
        Assert.All(rows, r => Assert.StartsWith("Changed_", r.Name)); // Name atualizado
        Assert.All(rows, r => Assert.NotEqual(999, r.Age));           // Age preservado (fora de UpdateColumns)
    }

    [Fact]
    public async Task BulkInsertIfNotExists_KeepsExisting_InsertsNew()
    {
        int[] existingIds;
        int maxId;
        using (var seed = new TestContext(Opts()))
        {
            var seeded = BuildCustomers(5);
            await seed.AddRangeAsync(seeded);
            await seed.SaveChangesAsync();
            existingIds = seeded.Select(s => s.Id).ToArray();
            maxId = seeded.Max(s => s.Id);
        }

        var batch = new List<Customer>();
        foreach (var id in existingIds)
            batch.Add(new Customer { Id = id, Name = "SHOULD_NOT_APPLY", Age = 1, Status = PersonStatus.Inactive, CustomerCode = "X", Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" } });
        for (int i = 1; i <= 3; i++)
            batch.Add(new Customer { Id = maxId + i, Name = $"New_{i}", Age = 1, Status = PersonStatus.Active, CustomerCode = "N", Contato = new ContatoPerson { Email = $"new{i}@x.com", Telefone = "0" } });

        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertIfNotExistsAsync(batch);

        using var verify = new TestContext(Opts());
        Assert.Equal(8, await verify.People.CountAsync());
        Assert.Equal(0, await verify.People.Where(p => p.Name == "SHOULD_NOT_APPLY").CountAsync()); // existentes intactos
        Assert.Equal(3, await verify.People.Where(p => p.Id > maxId).CountAsync());                 // novos inseridos
    }
}
