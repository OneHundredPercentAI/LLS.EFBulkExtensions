using LLS.EFBulkExtensions.Core.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace LLS.EFBulkExtensions.Tests.SqlServer;

/// <summary>
/// Compara a memória RETIDA pelo caminho DataTable (todas as linhas vivas ao mesmo tempo)
/// contra o EntityDataReader (streaming, uma linha por vez). Não acessa banco — só o modelo do EF.
/// </summary>
[Trait("Category", "Performance")]
public class StreamingMemoryTests
{
    private readonly ITestOutputHelper _output;
    public StreamingMemoryTests(ITestOutputHelper output) => _output = output;

    private static TestContext NewModelContext()
    {
        // String de conexão fictícia: o modelo é construído sem abrir conexão.
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new TestContext(options);
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
                Status = PersonStatus.Active,
                CustomerCode = $"C{i:0000000}",
                Contato = new ContatoPerson { Email = $"user{i}@example.com", Telefone = $"55119{i:00000000}" }
            });
        }
        return list;
    }

    private static long RetainedBytes(Func<object> build)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        var artifact = build();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);

        GC.KeepAlive(artifact);
        return after - before;
    }

    [Fact]
    public void DataReader_RetainsFarLessMemoryThanDataTable()
    {
        const int count = 200_000;
        using var ctx = NewModelContext();
        var list = BuildCustomers(count);

        // DataTable: materializa e retém todas as linhas.
        var dataTableBytes = RetainedBytes(() =>
        {
            var (table, _) = BulkMapper.Build(ctx, list, includeIdentity: false);
            return table;
        });

        // EntityDataReader: drena tudo (streaming); ao final retém apenas o reader + 1 buffer de linha.
        var readerBytes = RetainedBytes(() =>
        {
            var (columns, _, _) = BulkMapper.BuildColumns(ctx, list, includeIdentity: false);
            var reader = new EntityDataReader<Customer>(list, columns);
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++) _ = reader.GetValue(i);
            }
            return reader;
        });

        _output.WriteLine($"Linhas: {count:N0}");
        _output.WriteLine($"DataTable retido:      {dataTableBytes / 1024.0 / 1024.0,8:N2} MB");
        _output.WriteLine($"EntityDataReader retido:{readerBytes / 1024.0 / 1024.0,8:N2} MB");
        _output.WriteLine($"Redução: {100.0 * (1 - (double)readerBytes / dataTableBytes):N1}%");

        // O reader deve reter uma fração mínima do que a DataTable retém.
        Assert.True(readerBytes < dataTableBytes / 10,
            $"Esperado reader << DataTable. DataTable={dataTableBytes}, reader={readerBytes}");
    }
}
