using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LLS.EFBulkExtensions.Extensions;

public static class SequenceModelExtensions
{
    internal const string ReturnGeneratedIdsAnnotation = "LLS:ReturnGeneratedIds";

    /// <summary>
    /// Marca a propriedade como gerada na inserção (ValueGeneratedOnAdd) e, opcionalmente,
    /// habilita o retorno do ID gerado para esta entidade no BulkInsert (a nível de modelo).
    /// Opções de execução como BatchSize/Timeout devem ser passadas em <c>BulkInsertOptions</c>.
    /// </summary>
    public static PropertyBuilder ValueGeneratedOnAdd(
        this PropertyBuilder builder,
        bool? ReturnGeneratedIds = null)
    {
        builder.ValueGeneratedOnAdd();
        if (ReturnGeneratedIds.HasValue) builder.HasAnnotation(ReturnGeneratedIdsAnnotation, ReturnGeneratedIds.Value);
        return builder;
    }

    /// <inheritdoc cref="ValueGeneratedOnAdd(PropertyBuilder, bool?)"/>
    public static PropertyBuilder<TProperty> ValueGeneratedOnAdd<TProperty>(
        this PropertyBuilder<TProperty> builder,
        bool? ReturnGeneratedIds = null)
    {
        builder.ValueGeneratedOnAdd();
        if (ReturnGeneratedIds.HasValue) builder.HasAnnotation(ReturnGeneratedIdsAnnotation, ReturnGeneratedIds.Value);
        return builder;
    }
}
