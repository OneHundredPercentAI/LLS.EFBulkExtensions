using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LLS.EFBulkExtensions.Tests.Sqlite;

public class SQLiteTests
{
    private static readonly int _count = 1000_000;
    private static readonly int _batchSize = 10_000;

    private static DbContextOptions<TestSqliteContext> BuildOptions()
    {
        var builder = new DbContextOptionsBuilder<TestSqliteContext>()
            //.UseSqlite("Data Source=:memory:");
            .UseSqlite("Data Source=c:\\temp\\sqlite.db");
        return builder.Options;
    }

    private static async Task<TestSqliteContext> CreateContextAsync()
    {
        var options = BuildOptions();
        var context = new TestSqliteContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    [Fact]
    public async Task Insert_ShouldWork_Sqlite()
    {
        await using var context = await CreateContextAsync();

        var people = new List<Person>(_count);
        for (int i = 0; i < _count; i++)
        {
            people.Add(new Customer
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

        var options = new BulkInsertOptions
        {
            ReturnGeneratedIds = false,
            BatchSize = _batchSize,
            TimeoutSeconds = 120,
            PreserveIdentity = false,
            UseInternalTransaction = true,
            KeepNulls = false,
            UseAppLock = false
        };

        await context.BulkInsertAsync(people, options);

        var dbCount = await context.People.CountAsync();
        Assert.Equal(_count, dbCount);
        var defaultCount = await context.People.Where(p => p.ValorPadrao == 50).CountAsync();
        Assert.Equal(_count, defaultCount);
    }

    [Fact]
    public async Task Update_ShouldWork_Sqlite()
    {
        await using var context = await CreateContextAsync();

        // Seed
        var people = new List<Person>(_count);
        for (int i = 0; i < _count; i++)
        {
            people.Add(new Customer
            {
                Name = $"Person_{i}",
                Age = i % 100,
                Status = PersonStatus.Active,
                Contato = new ContatoPerson
                {
                    Email = $"user{i}@example.com",
                    Telefone = $"55119{i:00000000}"
                }
            });
        }
        await context.AddRangeAsync(people);
        await context.SaveChangesAsync();

        var peopleToUpdate = await context.People.OrderBy(p => p.Id).Take(_count).ToListAsync();
        foreach (var p in peopleToUpdate)
        {
            p.Name += "_U";
            p.Status = PersonStatus.Inactive;
            p.Contato.Email = "upd_" + p.Contato.Email;
        }

        await context.BulkUpdateAsync(peopleToUpdate, new BulkUpdateOptions
        {
            BatchSize = _batchSize,
            TimeoutSeconds = 120,
            UseInternalTransaction = true
        });

        var updatedCount = await context.People
            .Where(p => p.Name.EndsWith("_U") && p.Status == PersonStatus.Inactive && p.Contato.Email.StartsWith("upd_"))
            .CountAsync();
        Assert.Equal(_count, updatedCount);
    }

    [Fact]
    public async Task Delete_ShouldWork_Sqlite()
    {
        await using var context = await CreateContextAsync();

        // Seed
        var people = new List<Person>(_count);
        for (int i = 0; i < _count; i++)
        {
            people.Add(new Customer
            {
                Name = $"Person_{i}",
                Age = i % 100,
                Status = PersonStatus.Active,
                Contato = new ContatoPerson
                {
                    Email = $"user{i}@example.com",
                    Telefone = $"55119{i:00000000}"
                }
            });
        }
        await context.AddRangeAsync(people);
        await context.SaveChangesAsync();

        var initial = await context.People.CountAsync();
        var toDelete = await context.People.OrderBy(p => p.Id)
            .Select(o => new Customer
            {
                Id = o.Id,
                Contato = new ContatoPerson { Email = "", Telefone = "" }
            })
            .Take(_count)
            .ToListAsync();

        await context.BulkDeleteAsync(toDelete, new BulkDeleteOptions
        {
            BatchSize = 1_000,
            TimeoutSeconds = 120,
            UseInternalTransaction = true
        });

        var after = await context.People.CountAsync();
        Assert.True(after <= initial - _count);
    }

}

public class TestSqliteContext(DbContextOptions<TestSqliteContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PersonConfiguration());
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());
    }
}

public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("people");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Age);
        builder.Property(p => p.ValorPadrao).HasDefaultValue(50);
        builder.Property(p => p.Status).HasConversion(new EnumToStringConverter<PersonStatus>()).IsRequired();
        builder.OwnsOne(p => p.Contato, cb =>
        {
            cb.Property(c => c.Email).HasColumnName("contato_email").HasMaxLength(100).IsRequired();
            cb.Property(c => c.Telefone).HasColumnName("contato_telefone").HasMaxLength(20);
        });
    }
}

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasBaseType<Person>();
        builder.Property(c => c.CustomerCode).HasMaxLength(50).IsRequired();
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

public class Customer : Person
{
    public string CustomerCode { get; set; } = string.Empty;
}

