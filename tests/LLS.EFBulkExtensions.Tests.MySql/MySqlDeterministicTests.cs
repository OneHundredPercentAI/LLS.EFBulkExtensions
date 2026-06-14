using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LLS.EFBulkExtensions.Tests.MySql;

/// <summary>
/// Testes de integração determinísticos contra um MySQL local (cobre MariaDB pelo mesmo provider).
/// </summary>
public class MySqlDeterministicTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=127.0.0.1;Port=3306;Database=BulkDetTestDb;User ID=root;Password=abc1234$;AllowLoadLocalInfile=true";

    private static readonly ServerVersion Version = ServerVersion.AutoDetect(ConnectionString);

    private static DbContextOptions<TestContext> Opts() =>
        new DbContextOptionsBuilder<TestContext>().UseMySql(ConnectionString, Version).Options;

    public async Task InitializeAsync()
    {
        using var ctx = new TestContext(Opts());
        await ctx.Database.EnsureCreatedAsync();
        // MySqlBulkCopy usa LOAD DATA LOCAL INFILE; o servidor precisa de local_infile habilitado.
        await ctx.Database.ExecuteSqlRawAsync("SET GLOBAL local_infile = 1;");
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM people;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static List<Person> BuildPeople(int count)
    {
        var list = new List<Person>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new Person
            {
                Name = $"Person_{i}",
                Age = i % 100,
                Status = i % 2 == 0 ? PersonStatus.Active : PersonStatus.Inactive,
                Contato = new ContatoPerson { Email = $"user{i}@example.com", Telefone = $"55119{i:00000000}" }
            });
        }
        return list;
    }

    [Fact]
    public async Task BulkInsert_PersistsAllRows()
    {
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(BuildPeople(50));

        using var verify = new TestContext(Opts());
        Assert.Equal(50, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_PersistsOwnedTypeAndEnum()
    {
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(BuildPeople(10));

        using var verify = new TestContext(Opts());
        var first = await verify.People.OrderBy(p => p.Id).FirstAsync();
        Assert.Equal("user0@example.com", first.Contato.Email);
        Assert.Equal(5, await verify.People.Where(p => p.Status == PersonStatus.Active).CountAsync());
        Assert.Equal(10, await verify.People.Where(p => p.ValorPadrao == 50).CountAsync());
    }

    [Fact]
    public async Task BulkInsert_ReturnGeneratedIds_Throws()
    {
        using var ctx = new TestContext(Opts());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ctx.BulkInsertAsync(BuildPeople(5), new BulkInsertOptions { ReturnGeneratedIds = true }));
    }

    [Fact]
    public async Task BulkInsert_EmptyCollection_DoesNothing()
    {
        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertAsync(new List<Person>());

        using var verify = new TestContext(Opts());
        Assert.Equal(0, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkInsert_WithinTransaction_Rollback_PersistsNothing()
    {
        using (var ctx = new TestContext(Opts()))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await ctx.BulkInsertAsync(BuildPeople(10), new BulkInsertOptions { UseInternalTransaction = false });
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
            await ctx.BulkInsertAsync(BuildPeople(10), new BulkInsertOptions { UseInternalTransaction = false });
            await tx.CommitAsync();
        }

        using var verify = new TestContext(Opts());
        Assert.Equal(10, await verify.People.CountAsync());
    }

    [Fact]
    public async Task BulkUpdate_AppliesChanges()
    {
        using (var seed = new TestContext(Opts()))
        {
            await seed.AddRangeAsync(BuildPeople(20));
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
            await seed.AddRangeAsync(BuildPeople(20));
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
    public async Task BulkInsertOrUpdate_UpdatesExisting_AndInsertsNew()
    {
        int[] existingIds;
        int maxSeededId;
        using (var seed = new TestContext(Opts()))
        {
            var seeded = BuildPeople(10);
            await seed.AddRangeAsync(seeded);
            await seed.SaveChangesAsync();
            existingIds = seeded.Take(5).Select(s => s.Id).ToArray();
            maxSeededId = seeded.Max(s => s.Id);
        }

        var upsert = new List<Person>();
        foreach (var id in existingIds)
        {
            upsert.Add(new Person { Id = id, Name = $"Person_{id}_U", Age = 1, Status = PersonStatus.Inactive, Contato = new ContatoPerson { Email = "upd@x.com", Telefone = "0" } });
        }
        for (int i = 1; i <= 3; i++)
        {
            upsert.Add(new Person { Id = maxSeededId + i, Name = $"New_{i}", Age = 1, Status = PersonStatus.Active, Contato = new ContatoPerson { Email = $"new{i}@x.com", Telefone = "0" } });
        }

        using (var ctx = new TestContext(Opts()))
            await ctx.BulkInsertOrUpdateAsync(upsert);

        using var verify = new TestContext(Opts());
        Assert.Equal(13, await verify.People.CountAsync());
        Assert.Equal(5, await verify.People.Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive).CountAsync());
        Assert.Equal(3, await verify.People.Where(p => p.Id > maxSeededId).CountAsync());
    }
}

public class TestContext(DbContextOptions<TestContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PersonConfiguration());
    }
}

public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("people");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(p => p.Age).HasColumnName("age");
        builder.Property(p => p.ValorPadrao).HasColumnName("valor_padrao").HasDefaultValue(50);
        builder.Property(p => p.Status).HasColumnName("status").HasConversion(new EnumToStringConverter<PersonStatus>()).HasMaxLength(20).IsRequired();
        builder.OwnsOne(p => p.Contato, cb =>
        {
            cb.Property(c => c.Email).HasColumnName("contato_email").HasMaxLength(100).IsRequired();
            cb.Property(c => c.Telefone).HasColumnName("contato_telefone").HasMaxLength(20);
        });
    }
}

public enum PersonStatus { Active = 1, Inactive = 2 }

public class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Age { get; set; }
    public decimal ValorPadrao { get; set; }
    public PersonStatus Status { get; set; } = PersonStatus.Active;
    public ContatoPerson Contato { get; set; } = new();
}

public class ContatoPerson
{
    public string Email { get; set; } = null!;
    public string Telefone { get; set; } = null!;
}
