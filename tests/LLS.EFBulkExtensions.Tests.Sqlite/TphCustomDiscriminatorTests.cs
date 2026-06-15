using LLS.EFBulkExtensions.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Tests.Sqlite;

/// <summary>
/// Garante que o bulk insert grava o valor de discriminador TPH configurado no modelo
/// (HasDiscriminator().HasValue("...")), e não o nome curto do tipo CLR.
/// </summary>
public class TphCustomDiscriminatorTests
{
    [Fact]
    public async Task BulkInsert_HonorsCustomTphDiscriminatorValues()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;

        var options = new DbContextOptionsBuilder<VehicleContext>().UseSqlite(connection).Options;
        await using var ctx = new VehicleContext(options);
        await ctx.Database.EnsureCreatedAsync();

        var vehicles = new List<Vehicle>
        {
            new Car { Plate = "AAA1", Doors = 4 },
            new Truck { Plate = "BBB2", Capacity = 10.5 },
            new Car { Plate = "CCC3", Doors = 2 },
        };
        await ctx.BulkInsertAsync(vehicles);

        // 1) O valor cru gravado deve ser o customizado (CAR/TRK), não o nome do tipo CLR (Car/Truck).
        var stored = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT vtype FROM vehicles ORDER BY Id";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) stored.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "CAR", "TRK", "CAR" }, stored);

        // 2) Round-trip: o EF materializa cada linha no tipo correto a partir do discriminador.
        var fromDb = await ctx.Set<Vehicle>().OrderBy(v => v.Id).ToListAsync();
        Assert.IsType<Car>(fromDb[0]);
        Assert.IsType<Truck>(fromDb[1]);
        Assert.IsType<Car>(fromDb[2]);
    }
}

public class VehicleContext(DbContextOptions<VehicleContext> options) : DbContext(options)
{
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vehicle>(b =>
        {
            b.ToTable("vehicles");
            b.HasKey(v => v.Id);
            b.Property(v => v.Id).ValueGeneratedOnAdd();
            b.Property(v => v.Plate).HasMaxLength(20).IsRequired();
            b.HasDiscriminator<string>("vtype")
                .HasValue<Car>("CAR")
                .HasValue<Truck>("TRK");
        });
    }
}

public abstract class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = string.Empty;
}

public class Car : Vehicle
{
    public int Doors { get; set; }
}

public class Truck : Vehicle
{
    public double Capacity { get; set; }
}
