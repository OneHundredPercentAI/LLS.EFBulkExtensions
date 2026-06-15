using System.Collections.Generic;

namespace LLS.EFBulkExtensions.Options;

public sealed class BulkInsertOrUpdateOptions
{
    public int BatchSize { get; init; } = 5000;
    public int TimeoutSeconds { get; init; } = 30;
    public bool UseInternalTransaction { get; init; } = false;

    /// <summary>
    /// Quando preenchido, apenas estas colunas são atualizadas no ramo de UPDATE do upsert
    /// (as demais colunas das linhas existentes ficam intactas). Aceita nome da propriedade
    /// (ex.: <c>nameof(Person.Status)</c>) ou nome da coluna. Não altera o ramo de INSERT.
    /// Vazio/null = comportamento padrão (atualiza todas as colunas atualizáveis).
    /// </summary>
    public IReadOnlyList<string>? UpdateColumns { get; init; }

    /// <summary>
    /// Colunas a NÃO atualizar no ramo de UPDATE do upsert (ex.: <c>CreatedAt</c>). Aplicado
    /// depois de <see cref="UpdateColumns"/>. Aceita nome da propriedade ou da coluna.
    /// </summary>
    public IReadOnlyList<string>? ExcludeUpdateColumns { get; init; }

    /// <summary>
    /// Insere apenas as linhas cuja chave ainda não existe; linhas já existentes são ignoradas
    /// (nenhum UPDATE). Equivale a <c>ON CONFLICT DO NOTHING</c> / <c>INSERT IGNORE</c> /
    /// <c>MERGE ... WHEN NOT MATCHED</c>. Quando true, <see cref="UpdateColumns"/> e
    /// <see cref="ExcludeUpdateColumns"/> são ignoradas. Prefira o atalho
    /// <c>BulkInsertIfNotExistsAsync</c>.
    /// </summary>
    public bool InsertIfNotExists { get; init; } = false;

    /// <summary>
    /// Correlaciona por uma chave natural (nomes de propriedade) em vez da chave primária.
    /// Essas colunas precisam ter um índice/constraint <b>ÚNICO</b> no modelo (PK, alternate key
    /// ou índice único) — o upsert usa o mecanismo nativo (<c>ON CONFLICT</c> / <c>ON DUPLICATE
    /// KEY</c> / <c>MERGE</c>). Quando usado, a PK gerada pelo banco não é inserida (o banco a
    /// gera) e as próprias colunas de match não entram no UPDATE. Vazio/null = correlaciona por PK.
    /// </summary>
    public IReadOnlyList<string>? MatchProperties { get; init; }
}
