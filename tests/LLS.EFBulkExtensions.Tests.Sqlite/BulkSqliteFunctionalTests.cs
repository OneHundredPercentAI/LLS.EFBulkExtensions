using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Tests.Sqlite;

/// <summary>
/// Testes funcionais determinísticos em SQLite in-memory.
/// Reaproveita os modelos definidos em <see cref="TestSqliteContext"/> (Person/Customer/owned/enum/default/TPH).
/// Cada teste usa uma conexão própria mantida aberta para isolar o banco :memory:.
/// </summary>
public class BulkSqliteFunctionalTests
{
    private static async Task<(TestSqliteContext Context, SqliteConnection Connection)> CreateContextAsync()
    {
        // Conexão explícita mantida aberta => o banco :memory: sobrevive enquanto a conexão existir.
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestSqliteContext>()
            .UseSqlite(connection)
            .Options;

        var context = new TestSqliteContext(options);
        await context.Database.EnsureCreatedAsync();
        return (context, connection);
    }

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
                Contato = new ContatoPerson
                {
                    Email = $"user{i}@example.com",
                    Telefone = $"55119{i:00000000}"
                }
            });
        }
        return list;
    }

    [Fact]
    public async Task BulkInsert_PersistsAllRows()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var people = BuildCustomers(50);
        await context.BulkInsertAsync(people);

        Assert.Equal(50, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_AppliesMetadataDefaultValue()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        // ValorPadrao não é setado => deve cair no default do modelo (50).
        await context.BulkInsertAsync(BuildCustomers(10));

        Assert.Equal(10, await context.People.Where(p => p.ValorPadrao == 50).CountAsync());
    }

    [Fact]
    public async Task BulkInsert_PersistsOwnedType()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await context.BulkInsertAsync(BuildCustomers(10));

        var first = await context.People.OrderBy(p => p.Id).FirstAsync();
        Assert.Equal("user0@example.com", first.Contato.Email);
        Assert.Equal("5511900000000", first.Contato.Telefone);
    }

    [Fact]
    public async Task BulkInsert_PersistsEnumAsString()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await context.BulkInsertAsync(BuildCustomers(10));

        // Status é convertido via EnumToStringConverter.
        var activeCount = await context.People.Where(p => p.Status == PersonStatus.Active).CountAsync();
        Assert.Equal(5, activeCount);

        // Confirma que a coluna guarda o nome do enum, não o número.
        await using var raw = connection.CreateCommand();
        raw.CommandText = "SELECT COUNT(*) FROM people WHERE Status = 'Active'";
        var rawActive = Convert.ToInt32(await raw.ExecuteScalarAsync());
        Assert.Equal(5, rawActive);
    }

    [Fact]
    public async Task BulkInsert_SetsTphDiscriminator()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await context.BulkInsertAsync(BuildCustomers(10));

        // Todas as linhas inseridas como Customer devem ser materializadas como Customer.
        Assert.Equal(10, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_ReturnGeneratedIds_PopulatesEntityIds()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var people = BuildCustomers(10);
        Assert.All(people, p => Assert.Equal(0, p.Id));

        await context.BulkInsertAsync(people, new BulkInsertOptions { ReturnGeneratedIds = true });

        // Todos receberam Id, únicos e existentes no banco.
        Assert.All(people, p => Assert.True(p.Id > 0));
        Assert.Equal(10, people.Select(p => p.Id).Distinct().Count());

        var dbIds = await context.People.Select(p => p.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(people.Select(p => p.Id).OrderBy(x => x), dbIds);
    }

    [Fact]
    public async Task BulkUpdate_AppliesChanges()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var people = BuildCustomers(20);
        await context.AddRangeAsync(people);
        await context.SaveChangesAsync();

        var toUpdate = await context.People.OrderBy(p => p.Id).ToListAsync();
        foreach (var p in toUpdate)
        {
            p.Name += "_U";
            p.Status = PersonStatus.Inactive;
            p.Contato.Email = "upd_" + p.Contato.Email;
        }

        await context.BulkUpdateAsync(toUpdate);

        var updated = await context.People
            .Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive && p.Contato.Email.StartsWith("upd_"))
            .CountAsync();
        Assert.Equal(20, updated);
    }

    [Fact]
    public async Task BulkDelete_RemovesRows()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var people = BuildCustomers(20);
        await context.AddRangeAsync(people);
        await context.SaveChangesAsync();

        var toDelete = await context.People.OrderBy(p => p.Id).Take(8).ToListAsync();
        await context.BulkDeleteAsync(toDelete);

        Assert.Equal(12, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_EmptyCollection_DoesNothing()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await context.BulkInsertAsync(new List<Customer>());

        Assert.Equal(0, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsertOrUpdate_UpdatesExisting_AndInsertsNew()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(10);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();

        var upsert = new List<Customer>();
        // 5 existentes, modificados
        foreach (var s in seeded.Take(5))
        {
            upsert.Add(new Customer
            {
                Id = s.Id,
                Name = s.Name + "_U",
                Age = s.Age,
                Status = PersonStatus.Inactive,
                CustomerCode = s.CustomerCode,
                Contato = new ContatoPerson { Email = "upd_" + s.Contato.Email, Telefone = s.Contato.Telefone }
            });
        }
        // 3 novos, com IDs explícitos
        for (int i = 0; i < 3; i++)
        {
            upsert.Add(new Customer
            {
                Id = 1001 + i,
                Name = $"New_{i}",
                Age = 1,
                Status = PersonStatus.Active,
                CustomerCode = $"N{i}",
                Contato = new ContatoPerson { Email = $"new{i}@x.com", Telefone = "0" }
            });
        }

        await context.BulkInsertOrUpdateAsync(upsert);

        Assert.Equal(13, await context.People.CountAsync());
        Assert.Equal(5, await context.People.Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive).CountAsync());
        Assert.Equal(3, await context.People.Where(p => p.Id >= 1001).CountAsync());
    }

    // --- Transação ambiente do EF (mesma classe de bug do SQL Server) ---

    [Fact]
    public async Task BulkInsert_WithinTransaction_Rollback_PersistsNothing()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await using (var tx = await context.Database.BeginTransactionAsync())
        {
            await context.BulkInsertAsync(BuildCustomers(10), new BulkInsertOptions { UseInternalTransaction = false });
            await tx.RollbackAsync();
        }

        // Mesma conexão/banco :memory: => o rollback deve ter descartado tudo.
        Assert.Equal(0, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_WithinTransaction_Commit_Persists()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        await using (var tx = await context.Database.BeginTransactionAsync())
        {
            await context.BulkInsertAsync(BuildCustomers(10), new BulkInsertOptions { UseInternalTransaction = false });
            await tx.CommitAsync();
        }

        Assert.Equal(10, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_DefaultOptions_WithinTransaction_Works()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        // Default: UseInternalTransaction = true. Dentro de uma transação ambiente,
        // o inserter não pode tentar abrir uma transação aninhada.
        await using (var tx = await context.Database.BeginTransactionAsync())
        {
            await context.BulkInsertAsync(BuildCustomers(10));
            await tx.CommitAsync();
        }

        Assert.Equal(10, await context.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsertOrUpdate_UpdateColumns_OnlyUpdatesListedColumns()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(5);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();

        var upsert = seeded.Select(s => new Customer
        {
            Id = s.Id,
            Name = "Changed_" + s.Id,
            Age = 999,
            Status = PersonStatus.Inactive,
            CustomerCode = s.CustomerCode,
            Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" }
        }).ToList();

        await context.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions { UpdateColumns = new[] { nameof(Customer.Name) } });

        context.ChangeTracker.Clear(); // descarta os seeds rastreados; força releitura do banco
        var rows = await context.People.OrderBy(p => p.Id).ToListAsync();
        Assert.All(rows, r => Assert.StartsWith("Changed_", r.Name)); // Name atualizado
        Assert.All(rows, r => Assert.NotEqual(999, r.Age));           // Age preservado (fora de UpdateColumns)
    }

    [Fact]
    public async Task BulkInsertOrUpdate_ExcludeUpdateColumns_KeepsExcludedColumn()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(5);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();

        var upsert = seeded.Select(s => new Customer
        {
            Id = s.Id,
            Name = "Changed_" + s.Id,
            Age = 999,
            Status = PersonStatus.Inactive,
            CustomerCode = s.CustomerCode,
            Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" }
        }).ToList();

        // Exclui Age do UPDATE: Name muda, Age permanece o original.
        await context.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions { ExcludeUpdateColumns = new[] { nameof(Customer.Age) } });

        context.ChangeTracker.Clear(); // descarta os seeds rastreados; força releitura do banco
        var rows = await context.People.OrderBy(p => p.Id).ToListAsync();
        Assert.All(rows, r => Assert.StartsWith("Changed_", r.Name)); // Name atualizado (não excluído)
        Assert.All(rows, r => Assert.NotEqual(999, r.Age));           // Age preservado (ExcludeUpdateColumns)
    }

    [Fact]
    public async Task BulkInsertIfNotExists_KeepsExisting_InsertsNew()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(5);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();

        var batch = new List<Customer>();
        foreach (var s in seeded)
            batch.Add(new Customer { Id = s.Id, Name = "SHOULD_NOT_APPLY", Age = 1, Status = PersonStatus.Inactive, CustomerCode = s.CustomerCode, Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" } });
        for (int i = 0; i < 3; i++)
            batch.Add(new Customer { Id = 2001 + i, Name = $"New_{i}", Age = 1, Status = PersonStatus.Active, CustomerCode = $"N{i}", Contato = new ContatoPerson { Email = $"new{i}@x.com", Telefone = "0" } });

        await context.BulkInsertIfNotExistsAsync(batch);

        Assert.Equal(8, await context.People.CountAsync());
        Assert.Equal(0, await context.People.Where(p => p.Name == "SHOULD_NOT_APPLY").CountAsync()); // existentes intactos
        Assert.Equal(3, await context.People.Where(p => p.Id >= 2001).CountAsync());                 // novos inseridos
    }

    [Fact]
    public async Task BulkInsertOrUpdate_UnknownUpdateColumn_Throws()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(2);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();

        var upsert = seeded.Select(s => new Customer
        {
            Id = s.Id, Name = s.Name, Age = s.Age, Status = s.Status,
            CustomerCode = s.CustomerCode, Contato = new ContatoPerson { Email = "x@x.com", Telefone = "0" }
        }).ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.BulkInsertOrUpdateAsync(upsert, new BulkInsertOrUpdateOptions { UpdateColumns = new[] { "ColunaInexistente" } }));
        Assert.Contains("ColunaInexistente", ex.Message);
    }

    [Fact]
    public async Task BulkRead_FetchesRowsByPrimaryKey()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var seeded = BuildCustomers(10);
        await context.AddRangeAsync(seeded);
        await context.SaveChangesAsync();
        var existingIds = seeded.Take(5).Select(s => s.Id).ToArray();
        var maxId = seeded.Max(s => s.Id);
        context.ChangeTracker.Clear();

        // Chaves: 5 existentes + 2 inexistentes (devem ser ignoradas).
        var keyEntities = existingIds.Select(id => new Customer { Id = id })
            .Concat(new[] { new Customer { Id = maxId + 100 }, new Customer { Id = maxId + 200 } })
            .ToList();

        var found = await context.BulkReadAsync(keyEntities);

        Assert.Equal(5, found.Count);
        Assert.Equal(existingIds.OrderBy(x => x), found.Select(f => f.Id).OrderBy(x => x));
        // Materialização completa (owned type) confirma que os dados vieram do banco.
        Assert.All(found, f => Assert.False(string.IsNullOrEmpty(f.Contato.Email)));
    }

    [Fact]
    public async Task BulkRead_EmptyInput_ReturnsEmpty()
    {
        var (context, connection) = await CreateContextAsync();
        await using var _ = context;
        await using var __ = connection;

        var found = await context.BulkReadAsync(new List<Customer>());
        Assert.Empty(found);
    }
}
