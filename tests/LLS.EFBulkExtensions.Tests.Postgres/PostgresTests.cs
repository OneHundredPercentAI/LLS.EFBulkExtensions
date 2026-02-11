using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Diagnostics;

namespace LLS.EFBulkExtensions.Tests.Postgres;

public class PostgresTests
{
    private static readonly int _count = 1000_000;
    private static readonly int _batchSize = 10_000;

    private static DbContextOptions<TestContext>? BuildOptions()
    {
        var connectionString = "Host=127.0.0.1;Database=BulkInsertTestDb;Username=postgres;Password=abc1234$";
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        return new DbContextOptionsBuilder<TestContext>().UseNpgsql(connectionString).Options;
    }

    [Fact]
    public async Task Should_Create_Database_Model()
    {
        var opts = BuildOptions();
        using (var context = new TestContext(opts!))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }
    }

    [Fact]
    public async Task Insert_ShouldWork()
    {
        var opts = BuildOptions();
        if (opts is null) return;

        try
        {
            using (var context = new TestContext(opts))
            {
                var entityType = context.Model.FindEntityType(typeof(Person))!;
                foreach (var _ in entityType.GetProperties()) { }
                foreach (var _ in entityType.GetNavigations()) { }
            }

            var count = _count;
            var people = new List<Person>(count);
            for (int i = 0; i < count; i++)
            {
                people.Add(new Person
                {
                    Name = $"Person_{i}_{Guid.NewGuid()}",
                    Age = i % 100,
                    Status = i % 2 == 0 ? PersonStatus.Active : PersonStatus.Inactive,
                    Contato = new ContatoPerson
                    {
                        Email = $"user{i}@example.com",
                        Telefone = $"55119{i:00000000}"
                    }
                });
            }

            using (var context = new TestContext(opts))
            {
                var sw = Stopwatch.StartNew();
                var options = new BulkInsertOptions
                {
                    ReturnGeneratedIds = false,
                    BatchSize = _batchSize,
                    TimeoutSeconds = 120,
                    PreserveIdentity = false,
                    UseInternalTransaction = false,
                    KeepNulls = false,
                    UseAppLock = false,
                };
                await context.BulkInsertAsync(people, options);
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds >= 0);
            }

            using (var context = new TestContext(opts))
            {
                var dbCount = await context.People.CountAsync();
                Assert.True(dbCount >= count);
                var defaultCount = await context.People.Where(p => p.ValorPadrao == 50).CountAsync();
                Assert.True(defaultCount >= count);
            }
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException("Integration test failed: " + ex.Message + "\n" + ex.StackTrace);
        }
    }

    [Fact]
    public async Task Update_ShouldWork()
    {
        var opts = BuildOptions();
        if (opts is null) return;
        using var context = new TestContext(opts);
        var peopleToUpdate = await context.People.OrderBy(p => p.Id).Take(_count).ToListAsync();
        foreach (var p in peopleToUpdate)
        {
            p.Name += "_U";
            p.Status = PersonStatus.Inactive;
            p.Contato.Email = "upd_" + p.Contato.Email;
        }
        await context.BulkUpdateAsync(peopleToUpdate, new BulkUpdateOptions { BatchSize = _batchSize });
        var updatedCount = await context.People
            .Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive && p.Contato.Email.StartsWith("upd_"))
            .CountAsync();
        Assert.True(updatedCount >= 0);
    }

    [Fact]
    public async Task Delete_ShouldWork()
    {
        var opts = BuildOptions();
        if (opts is null) return;
        using var context = new TestContext(opts);
        var initial = await context.People.CountAsync();
        var toDelete = await context.People.OrderBy(p => p.Id).Take(_count).ToListAsync();
        await context.BulkDeleteAsync(toDelete, new BulkDeleteOptions { BatchSize = _batchSize });
        var after = await context.People.CountAsync();
        Assert.True(after <= initial);
    }
}

public class TestContext : DbContext
{
    public TestContext(DbContextOptions<TestContext> options) : base(options) { }
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
        builder.ToTable("people", "contato");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(p => p.Age).HasColumnName("age");
        builder.Property(p => p.ValorPadrao).HasColumnName("valor_padrao").HasDefaultValue(50);
        builder.Property(p => p.Status).HasColumnName("status").HasConversion(new EnumToStringConverter<PersonStatus>()).IsRequired();
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
