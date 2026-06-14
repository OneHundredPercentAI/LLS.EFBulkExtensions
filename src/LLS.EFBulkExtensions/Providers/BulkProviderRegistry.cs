using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Providers.Postgres;
using LLS.EFBulkExtensions.Providers.SqlServer;
using LLS.EFBulkExtensions.Providers.Sqlite;

namespace LLS.EFBulkExtensions.Providers;

/// <summary>
/// Agrega as implementações de bulk (insert/update/delete) de um provedor de banco.
/// </summary>
internal interface IBulkProvider
{
    IBulkInserter Inserter { get; }
    IBulkUpdater Updater { get; }
    IBulkDeleter Deleter { get; }
    IBulkUpserter Upserter { get; }
}

internal sealed class SqlServerBulkProvider : IBulkProvider
{
    public IBulkInserter Inserter { get; } = new SqlServerBulkInserter();
    public IBulkUpdater Updater { get; } = new SqlServerBulkUpdater();
    public IBulkDeleter Deleter { get; } = new SqlServerBulkDeleter();
    public IBulkUpserter Upserter { get; } = new SqlServerBulkUpserter();
}

internal sealed class PostgresBulkProvider : IBulkProvider
{
    public IBulkInserter Inserter { get; } = new PostgresBulkInserter();
    public IBulkUpdater Updater { get; } = new PostgresBulkUpdater();
    public IBulkDeleter Deleter { get; } = new PostgresBulkDeleter();
    public IBulkUpserter Upserter { get; } = new PostgresBulkUpserter();
}

internal sealed class SqliteBulkProvider : IBulkProvider
{
    public IBulkInserter Inserter { get; } = new SqliteBulkInserter();
    public IBulkUpdater Updater { get; } = new SqliteBulkUpdater();
    public IBulkDeleter Deleter { get; } = new SqliteBulkDeleter();
    public IBulkUpserter Upserter { get; } = new SqliteBulkUpserter();
}

/// <summary>
/// Resolve o <see cref="IBulkProvider"/> a partir do ProviderName do EF Core.
/// Para adicionar suporte a um novo banco, registre uma entrada aqui.
/// As implementações são stateless, por isso são compartilhadas como singletons.
/// </summary>
internal static class BulkProviderRegistry
{
    private static readonly IReadOnlyDictionary<string, IBulkProvider> Providers =
        new Dictionary<string, IBulkProvider>(StringComparer.Ordinal)
        {
            ["Microsoft.EntityFrameworkCore.SqlServer"] = new SqlServerBulkProvider(),
            ["Npgsql.EntityFrameworkCore.PostgreSQL"] = new PostgresBulkProvider(),
            ["Microsoft.EntityFrameworkCore.Sqlite"] = new SqliteBulkProvider(),
        };

    public static IBulkProvider Resolve(DbContext context)
    {
        var name = context.Database.ProviderName;
        if (name != null && Providers.TryGetValue(name, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"Provedor de banco de dados não suportado: {name ?? "(desconhecido)"}");
    }
}
