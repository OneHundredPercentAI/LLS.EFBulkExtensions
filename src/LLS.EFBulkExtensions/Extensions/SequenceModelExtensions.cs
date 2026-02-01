using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
using System;

namespace LLS.EFBulkExtensions.Extensions;
public static class SequenceModelExtensions
{
    private const string BatchSizeAnnotation = "Trae:BatchSize";
    private const string TimeoutAnnotation = "Trae:TimeoutSeconds";
    private const string PreserveIdentityAnnotation = "Trae:PreserveIdentity";
    private const string UseInternalTransactionAnnotation = "Trae:UseInternalTransaction";
    private const string KeepNullsAnnotation = "Trae:KeepNulls";
    private const string ReturnGeneratedIdsAnnotation = "Trae:ReturnGeneratedIds";

    public static PropertyBuilder ValueGeneratedOnAdd(
        this PropertyBuilder builder, 
        bool? ReturnGeneratedIds = null,
        int? BatchSize = null,
        int? TimeoutSeconds = null,
        bool? PreserveIdentity = null,
        bool? UseInternalTransaction = null,
        bool? KeepNulls = null)
    {
        builder.ValueGeneratedOnAdd();
        if (ReturnGeneratedIds.HasValue) builder.HasAnnotation(ReturnGeneratedIdsAnnotation, ReturnGeneratedIds.Value);
        if (BatchSize.HasValue) builder.HasAnnotation(BatchSizeAnnotation, BatchSize.Value);
        if (TimeoutSeconds.HasValue) builder.HasAnnotation(TimeoutAnnotation, TimeoutSeconds.Value);
        if (PreserveIdentity.HasValue) builder.HasAnnotation(PreserveIdentityAnnotation, PreserveIdentity.Value);
        if (UseInternalTransaction.HasValue) builder.HasAnnotation(UseInternalTransactionAnnotation, UseInternalTransaction.Value);
        if (KeepNulls.HasValue) builder.HasAnnotation(KeepNullsAnnotation, KeepNulls.Value);
        return builder;
    }

    public static PropertyBuilder<TProperty> ValueGeneratedOnAdd<TProperty>(
        this PropertyBuilder<TProperty> builder,
        bool? ReturnGeneratedIds = null,
        int? BatchSize = null,
        int? TimeoutSeconds = null,
        bool? PreserveIdentity = null,
        bool? UseInternalTransaction = null,
        bool? KeepNulls = null)
    {
        builder.ValueGeneratedOnAdd();
        if (ReturnGeneratedIds.HasValue) builder.HasAnnotation(ReturnGeneratedIdsAnnotation, ReturnGeneratedIds.Value);
        if (BatchSize.HasValue) builder.HasAnnotation(BatchSizeAnnotation, BatchSize.Value);
        if (TimeoutSeconds.HasValue) builder.HasAnnotation(TimeoutAnnotation, TimeoutSeconds.Value);
        if (PreserveIdentity.HasValue) builder.HasAnnotation(PreserveIdentityAnnotation, PreserveIdentity.Value);
        if (UseInternalTransaction.HasValue) builder.HasAnnotation(UseInternalTransactionAnnotation, UseInternalTransaction.Value);
        if (KeepNulls.HasValue) builder.HasAnnotation(KeepNullsAnnotation, KeepNulls.Value);
        return builder;
    }

    // Removed enum-based overloads and Sequence-specific helpers

    // Removed enum-based overloads
}
