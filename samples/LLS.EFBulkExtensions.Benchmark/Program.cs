using System.Globalization;
using LLS.EFBulkExtensions.Benchmark;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var ci = CultureInfo.InvariantCulture;
var targets = Defaults.Targets();

Console.WriteLine("===========================================");
Console.WriteLine("   BulkInsert Benchmark — LLS.EFBulkExtensions");
Console.WriteLine("===========================================");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("Bancos disponíveis:");
    for (int i = 0; i < targets.Count; i++)
        Console.WriteLine($"  {i + 1}) {targets[i].Name}");
    Console.WriteLine("  0) Sair");
    Console.WriteLine();
    Console.Write("Selecione (ex: 1  |  1,3,4 para comparar  |  all): ");
    var sel = (Console.ReadLine() ?? "").Trim();

    if (sel is "0" or "sair" or "exit" or "q") break;

    List<DbTarget> chosen;
    if (sel.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        chosen = targets.ToList();
    }
    else
    {
        chosen = sel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 1 && n <= targets.Count)
            .Distinct()
            .Select(n => targets[n - 1])
            .ToList();
    }

    if (chosen.Count == 0)
    {
        Console.WriteLine("Seleção inválida.");
        continue;
    }

    // Permite ajustar a connection string de cada banco escolhido.
    for (int i = 0; i < chosen.Count; i++)
    {
        Console.Write($"Connection string [{chosen[i].Name}] (Enter mantém padrão):\n  {chosen[i].ConnectionString}\n  > ");
        var cs = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(cs))
            chosen[i] = chosen[i] with { ConnectionString = cs.Trim() };
    }

    int rows = AskInt("Quantidade de registros", 100_000, min: 1);
    int cols = AskInt("Quantidade de colunas (além da PK Id)", 10, min: 1, max: 512);
    bool returnIds = AskYesNo("Retornar os IDs gerados (ReturnGeneratedIds)?", false);

    // Mesmo layout/tipo dinâmico para todos os bancos comparados → comparação justa.
    var entityType = DynamicEntityFactory.Create(cols);

    var results = new List<BenchResult>();
    foreach (var t in chosen)
    {
        Console.WriteLine();
        Console.WriteLine($">> {t.Name}: {rows:N0} registros x {cols} colunas ...");
        var r = await BenchmarkRunner.RunAsync(t, entityType, rows, cols, returnIds);
        results.Add(r);
        if (r.Error != null)
            Console.WriteLine($"   ERRO: {r.Error}");
        else
            Console.WriteLine($"   OK: insert {r.InsertMs:N0} ms | {RowsPerSec(r):N0} linhas/s | {r.RowsInDb:N0} linhas no banco");
    }

    PrintTable(results, returnIds, ci);
}

Console.WriteLine("Até mais.");
return;

static long RowsPerSec(BenchResult r) =>
    r.InsertMs > 0 ? (long)(r.Rows / (r.InsertMs / 1000.0)) : 0;

static int AskInt(string label, int def, int min = int.MinValue, int max = int.MaxValue)
{
    while (true)
    {
        Console.Write($"{label} [{def}]: ");
        var s = (Console.ReadLine() ?? "").Trim();
        if (s.Length == 0) return def;
        if (int.TryParse(s, out var v) && v >= min && v <= max) return v;
        Console.WriteLine($"  valor inválido (faixa {min}..{max}).");
    }
}

static bool AskYesNo(string label, bool def)
{
    Console.Write($"{label} ({(def ? "S/n" : "s/N")}): ");
    var s = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
    if (s.Length == 0) return def;
    return s is "s" or "sim" or "y" or "yes";
}

static void PrintTable(List<BenchResult> results, bool returnIds, CultureInfo ci)
{
    Console.WriteLine();
    Console.WriteLine("===== Resultado =====");
    var header = string.Format(ci, "{0,-12} {1,12} {2,8} {3,12} {4,12} {5,14}",
        "Banco", "Registros", "Colunas", "Gerar(ms)", "Insert(ms)", "linhas/s");
    if (returnIds) header += "  IDs";
    Console.WriteLine(header);
    Console.WriteLine(new string('-', returnIds ? header.Length : header.Length));

    foreach (var r in results)
    {
        if (r.Error != null)
        {
            Console.WriteLine(string.Format(ci, "{0,-12} {1}", r.Provider, "ERRO: " + Trunc(r.Error, 70)));
            continue;
        }
        var rps = r.InsertMs > 0 ? (long)(r.Rows / (r.InsertMs / 1000.0)) : 0;
        var line = string.Format(ci, "{0,-12} {1,12:N0} {2,8} {3,12:N0} {4,12:N0} {5,14:N0}",
            r.Provider, r.Rows, r.Columns, r.GenMs, r.InsertMs, rps);
        if (returnIds) line += "  " + (r.IdsReturned == true ? "sim" : "não");
        Console.WriteLine(line);
    }

    var ok = results.Where(r => r.Error == null && r.InsertMs > 0).ToList();
    if (ok.Count > 1)
    {
        var fastest = ok.OrderBy(r => r.InsertMs).First();
        Console.WriteLine();
        Console.WriteLine($"Mais rápido: {fastest.Provider} ({fastest.InsertMs:N0} ms).");
    }
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
