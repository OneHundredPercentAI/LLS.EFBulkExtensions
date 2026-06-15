using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace LLS.EFBulkExtensions.Benchmark;

/// <summary>
/// Gera, em runtime, um tipo CLR com uma PK <c>Id</c> (long, auto-incremento) e N colunas de dados
/// de tipos variados. Necessário porque a lib de bulk resolve as colunas pelo modelo do EF, que
/// exige um tipo concreto registrado — não dá para usar um property bag dinâmico.
/// </summary>
public static class DynamicEntityFactory
{
    // Tipos que se comportam bem em todos os providers (sem armadilhas de mapeamento cross-DB).
    private static readonly Type[] ColumnKinds =
    {
        typeof(int), typeof(string), typeof(decimal), typeof(DateTime), typeof(bool), typeof(long)
    };

    private static readonly ModuleBuilder Module;

    static DynamicEntityFactory()
    {
        var asmName = new AssemblyName("LLS.EFBulkExtensions.Benchmark.Dynamic");
        var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        Module = asm.DefineDynamicModule("Main");
    }

    /// <summary>Cria um tipo com Id + <paramref name="dataColumns"/> colunas (C0..Cn-1).</summary>
    public static Type Create(int dataColumns)
    {
        var tb = Module.DefineType(
            $"BenchEntity_{dataColumns}_{Guid.NewGuid():N}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass);

        AddAutoProperty(tb, "Id", typeof(long));
        for (int i = 0; i < dataColumns; i++)
        {
            AddAutoProperty(tb, "C" + i, ColumnKinds[i % ColumnKinds.Length]);
        }
        return tb.CreateType()!;
    }

    private static void AddAutoProperty(TypeBuilder tb, string name, Type type)
    {
        var field = tb.DefineField("_" + name, type, FieldAttributes.Private);
        var prop = tb.DefineProperty(name, PropertyAttributes.None, type, null);
        const MethodAttributes attrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        var getter = tb.DefineMethod("get_" + name, attrs, type, Type.EmptyTypes);
        var gil = getter.GetILGenerator();
        gil.Emit(OpCodes.Ldarg_0);
        gil.Emit(OpCodes.Ldfld, field);
        gil.Emit(OpCodes.Ret);

        var setter = tb.DefineMethod("set_" + name, attrs, null, new[] { type });
        var sil = setter.GetILGenerator();
        sil.Emit(OpCodes.Ldarg_0);
        sil.Emit(OpCodes.Ldarg_1);
        sil.Emit(OpCodes.Stfld, field);
        sil.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
        prop.SetSetMethod(setter);
    }
}

/// <summary>
/// Constrói listas tipadas (List&lt;TEntity&gt;) preenchidas com dados aleatórios, usando setters
/// compilados (rápido o suficiente para que a geração não domine o tempo de benchmark).
/// </summary>
public sealed class RowFactory
{
    private readonly Type _entityType;
    private readonly (Action<object, object?> Setter, Func<Random, object?> Gen)[] _columns;
    private readonly Func<object, long> _readId;

    public RowFactory(Type entityType)
    {
        _entityType = entityType;
        var props = entityType.GetProperties();
        _columns = props
            .Where(p => p.Name != "Id")
            .Select(p => (BuildSetter(p), BuildGenerator(p.PropertyType)))
            .ToArray();

        var idGet = entityType.GetProperty("Id")!.GetMethod!;
        var obj = Expression.Parameter(typeof(object));
        _readId = Expression.Lambda<Func<object, long>>(
            Expression.Call(Expression.Convert(obj, entityType), idGet), obj).Compile();
    }

    /// <summary>Gera <paramref name="count"/> linhas. Retorna um List&lt;TEntity&gt; (como IList).</summary>
    public IList Build(int count, int seed)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_entityType))!;
        var rnd = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var row = Activator.CreateInstance(_entityType)!;
            foreach (var (setter, gen) in _columns)
            {
                setter(row, gen(rnd));
            }
            list.Add(row);
        }
        return list;
    }

    public long ReadId(object entity) => _readId(entity);

    private static Action<object, object?> BuildSetter(PropertyInfo p)
    {
        var objParam = Expression.Parameter(typeof(object));
        var valParam = Expression.Parameter(typeof(object));
        var body = Expression.Call(
            Expression.Convert(objParam, p.DeclaringType!),
            p.SetMethod!,
            Expression.Convert(valParam, p.PropertyType));
        return Expression.Lambda<Action<object, object?>>(body, objParam, valParam).Compile();
    }

    private static Func<Random, object?> BuildGenerator(Type t)
    {
        if (t == typeof(int)) return r => r.Next(0, 1_000_000);
        if (t == typeof(long)) return r => (long)r.Next(0, 1_000_000) * 1000L + r.Next(0, 1000);
        if (t == typeof(decimal)) return r => Math.Round((decimal)(r.NextDouble() * 1_000_000), 2);
        if (t == typeof(bool)) return r => r.Next(0, 2) == 1;
        // DateTime com Kind=Unspecified (exigido pelo Npgsql para timestamp without time zone).
        if (t == typeof(DateTime)) return r => new DateTime(2000, 1, 1).AddSeconds(r.Next(0, 900_000_000));
        if (t == typeof(string)) return RandomString;
        throw new NotSupportedException($"Gerador não definido para o tipo {t}.");
    }

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";

    private static object RandomString(Random r)
    {
        var len = r.Next(5, 41);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(Alphabet[r.Next(Alphabet.Length)]);
        return sb.ToString();
    }
}
