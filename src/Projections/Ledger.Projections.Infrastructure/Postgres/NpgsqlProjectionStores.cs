using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Chart;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Snapshots;
using Ledger.Projections.Application.Taxes;
using Npgsql;

namespace Ledger.Projections.Infrastructure.Postgres;

// Ports d'écriture des projections sur PostgreSQL, dans la transaction de la session. Chaque
// table est nommée par la session (tables en service ou parallèles de reconstruction).

/// <summary><c>account_balances</c> : cumul atomique par <c>INSERT … ON CONFLICT DO UPDATE</c>.</summary>
public sealed class NpgsqlAccountBalanceStore(NpgsqlProjectionSession session) : IAccountBalanceStore
{
    public async Task AddAsync(string entityId, AccountMovement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);
        await session.ExecuteAsync(
            $"""
            INSERT INTO {session.Table(ProjectionTables.AccountBalances)} AS b (entity_id, account_id, currency, debit_total, credit_total)
            VALUES (@entity_id, @account_id, @currency, @debit, @credit)
            ON CONFLICT (entity_id, account_id, currency) DO UPDATE SET
                debit_total = b.debit_total + EXCLUDED.debit_total,
                credit_total = b.credit_total + EXCLUDED.credit_total
            """,
            parameters =>
            {
                parameters.AddWithValue("entity_id", entityId);
                parameters.AddWithValue("account_id", movement.AccountId);
                parameters.AddWithValue("currency", movement.Currency);
                parameters.AddWithValue("debit", movement.Debit);
                parameters.AddWithValue("credit", movement.Credit);
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>journal_entries</c> et <c>ledger_lines</c> : l'écriture et ses lignes en un seul lot.</summary>
public sealed class NpgsqlLedgerEntryStore(NpgsqlProjectionSession session) : ILedgerEntryStore
{
    public async Task AddAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var batch = session.CreateBatch();
        await using (batch.ConfigureAwait(false))
        {
            var header = new NpgsqlBatchCommand(
                $"""
                INSERT INTO {session.Table(ProjectionTables.JournalEntries)}
                    (entry_id, entity_id, business_date, period, description, reversal_of, reversed_by, global_sequence, recorded_at, actor)
                VALUES (@entry_id, @entity_id, @business_date, @period, @description, @reversal_of, NULL, @global_sequence, @recorded_at, @actor)
                """);
            header.Parameters.AddWithValue("entry_id", entry.EntryId);
            header.Parameters.AddWithValue("entity_id", entry.EntityId);
            header.Parameters.AddWithValue("business_date", entry.BusinessDate);
            header.Parameters.AddWithValue("period", entry.Period);
            header.Parameters.AddWithValue("description", (object?)entry.Description ?? DBNull.Value);
            header.Parameters.AddWithValue("reversal_of", (object?)entry.ReversalOf ?? DBNull.Value);
            header.Parameters.AddWithValue("global_sequence", entry.GlobalSequence);
            header.Parameters.AddWithValue("recorded_at", entry.RecordedAt);
            header.Parameters.AddWithValue("actor", entry.Actor);
            batch.BatchCommands.Add(header);

            foreach (var line in entry.Lines)
            {
                var insert = new NpgsqlBatchCommand(
                    $"""
                    INSERT INTO {session.Table(ProjectionTables.LedgerLines)}
                        (entry_id, line_number, entity_id, account_id, side, amount, currency, business_date, period, global_sequence)
                    VALUES (@entry_id, @line_number, @entity_id, @account_id, @side, @amount, @currency, @business_date, @period, @global_sequence)
                    """);
                insert.Parameters.AddWithValue("entry_id", entry.EntryId);
                insert.Parameters.AddWithValue("line_number", line.LineNumber);
                insert.Parameters.AddWithValue("entity_id", entry.EntityId);
                insert.Parameters.AddWithValue("account_id", line.AccountId);
                insert.Parameters.AddWithValue("side", line.Side);
                insert.Parameters.AddWithValue("amount", line.Amount);
                insert.Parameters.AddWithValue("currency", line.Currency);
                insert.Parameters.AddWithValue("business_date", entry.BusinessDate);
                insert.Parameters.AddWithValue("period", entry.Period);
                insert.Parameters.AddWithValue("global_sequence", entry.GlobalSequence);
                batch.BatchCommands.Add(insert);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MarkReversedAsync(Guid entryId, Guid reversalEntryId, CancellationToken cancellationToken)
    {
        var updated = await session.ExecuteAsync(
            $"UPDATE {session.Table(ProjectionTables.JournalEntries)} SET reversed_by = @reversed_by WHERE entry_id = @entry_id",
            parameters =>
            {
                parameters.AddWithValue("entry_id", entryId);
                parameters.AddWithValue("reversed_by", reversalEntryId);
            },
            cancellationToken).ConfigureAwait(false);

        // La contre-passation suit toujours l'écriture dans son flux (ADR 0003), donc dans la projection.
        if (updated != 1)
        {
            throw new InvalidOperationException($"Écriture contre-passée {entryId} absente de la projection.");
        }
    }
}

/// <summary><c>chart_of_accounts</c> : l'état complet du compte remplace le précédent (ADR 0012).</summary>
public sealed class NpgsqlChartOfAccountsStore(NpgsqlProjectionSession session) : IChartOfAccountsStore
{
    public async Task SaveAsync(ChartAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        await session.ExecuteAsync(
            $"""
            INSERT INTO {session.Table(ProjectionTables.ChartOfAccounts)}
                (entity_id, account_id, name, currency, normal_side, is_active, no_negative_balance, chart_version, defined_at)
            VALUES (@entity_id, @account_id, @name, @currency, @normal_side, @is_active, @no_negative_balance, @chart_version, @defined_at)
            ON CONFLICT (entity_id, account_id) DO UPDATE SET
                name = EXCLUDED.name,
                currency = EXCLUDED.currency,
                normal_side = EXCLUDED.normal_side,
                is_active = EXCLUDED.is_active,
                no_negative_balance = EXCLUDED.no_negative_balance,
                chart_version = EXCLUDED.chart_version,
                defined_at = EXCLUDED.defined_at
            """,
            parameters =>
            {
                parameters.AddWithValue("entity_id", account.EntityId);
                parameters.AddWithValue("account_id", account.AccountId);
                parameters.AddWithValue("name", account.Name);
                parameters.AddWithValue("currency", account.Currency);
                parameters.AddWithValue("normal_side", account.NormalSide);
                parameters.AddWithValue("is_active", account.IsActive);
                parameters.AddWithValue("no_negative_balance", account.NoNegativeBalance);
                parameters.AddWithValue("chart_version", account.ChartVersion);
                parameters.AddWithValue("defined_at", account.DefinedAt);
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>periods</c> : statut et empreinte du sceau.</summary>
public sealed class NpgsqlPeriodStatusStore(NpgsqlProjectionSession session) : IPeriodStatusStore
{
    public async Task OpenAsync(PeriodOpening opening, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opening);
        await session.ExecuteAsync(
            $"""
            INSERT INTO {session.Table(ProjectionTables.Periods)} (entity_id, period, status, version, opened_at)
            VALUES (@entity_id, @period, 'Open', @version, @opened_at)
            ON CONFLICT (entity_id, period) DO UPDATE SET version = EXCLUDED.version, opened_at = EXCLUDED.opened_at
            """,
            parameters =>
            {
                parameters.AddWithValue("entity_id", opening.EntityId);
                parameters.AddWithValue("period", opening.Period);
                parameters.AddWithValue("version", opening.Version);
                parameters.AddWithValue("opened_at", opening.OpenedAt);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SealAsync(PeriodSeal seal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seal);
        await session.ExecuteAsync(
            $"""
            INSERT INTO {session.Table(ProjectionTables.Periods)} (entity_id, period, status, version, sealed_at, event_count, merkle_root)
            VALUES (@entity_id, @period, 'Sealed', @version, @sealed_at, @event_count, @merkle_root)
            ON CONFLICT (entity_id, period) DO UPDATE SET
                status = 'Sealed',
                version = EXCLUDED.version,
                sealed_at = EXCLUDED.sealed_at,
                event_count = EXCLUDED.event_count,
                merkle_root = EXCLUDED.merkle_root
            """,
            parameters =>
            {
                parameters.AddWithValue("entity_id", seal.EntityId);
                parameters.AddWithValue("period", seal.Period);
                parameters.AddWithValue("version", seal.Version);
                parameters.AddWithValue("sealed_at", seal.SealedAt);
                parameters.AddWithValue("event_count", seal.EventCount);
                parameters.AddWithValue("merkle_root", (object?)seal.MerkleRoot ?? DBNull.Value);
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>period_snapshots</c> : lignes insérées une fois ; un second instantané de la même période est une erreur.</summary>
public sealed class NpgsqlPeriodSnapshotStore(NpgsqlProjectionSession session) : IPeriodSnapshotStore
{
    public async Task SaveAsync(PeriodSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Lines.Count == 0)
        {
            return;
        }

        var batch = session.CreateBatch();
        await using (batch.ConfigureAwait(false))
        {
            foreach (var line in snapshot.Lines)
            {
                var insert = new NpgsqlBatchCommand(
                    $"""
                    INSERT INTO {session.Table(ProjectionTables.PeriodSnapshots)}
                        (entity_id, period, account_id, currency, debits, credits, seal_sequence, event_count)
                    VALUES (@entity_id, @period, @account_id, @currency, @debits, @credits, @seal_sequence, @event_count)
                    """);
                insert.Parameters.AddWithValue("entity_id", snapshot.EntityId);
                insert.Parameters.AddWithValue("period", snapshot.Period);
                insert.Parameters.AddWithValue("account_id", line.AccountId);
                insert.Parameters.AddWithValue("currency", line.Currency);
                insert.Parameters.AddWithValue("debits", line.Debits);
                insert.Parameters.AddWithValue("credits", line.Credits);
                insert.Parameters.AddWithValue("seal_sequence", snapshot.SealSequence);
                insert.Parameters.AddWithValue("event_count", snapshot.EventCount);
                batch.BatchCommands.Add(insert);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary><c>tax_projection</c> : une colonne par rôle fiscal, cumulée.</summary>
public sealed class NpgsqlTaxProjectionStore(NpgsqlProjectionSession session) : ITaxProjectionStore
{
    public async Task AddAsync(TaxMovement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);
        await session.ExecuteAsync(
            $"""
            INSERT INTO {session.Table(ProjectionTables.TaxProjection)} AS t
                (entity_id, period, currency, gst_collected, gst_paid, qst_collected, qst_paid)
            VALUES (@entity_id, @period, @currency, @gst_collected, @gst_paid, @qst_collected, @qst_paid)
            ON CONFLICT (entity_id, period, currency) DO UPDATE SET
                gst_collected = t.gst_collected + EXCLUDED.gst_collected,
                gst_paid = t.gst_paid + EXCLUDED.gst_paid,
                qst_collected = t.qst_collected + EXCLUDED.qst_collected,
                qst_paid = t.qst_paid + EXCLUDED.qst_paid
            """,
            parameters =>
            {
                parameters.AddWithValue("entity_id", movement.EntityId);
                parameters.AddWithValue("period", movement.Period);
                parameters.AddWithValue("currency", movement.Currency);
                parameters.AddWithValue("gst_collected", movement.Kind == TaxKind.GstCollected ? movement.Amount : 0m);
                parameters.AddWithValue("gst_paid", movement.Kind == TaxKind.GstPaid ? movement.Amount : 0m);
                parameters.AddWithValue("qst_collected", movement.Kind == TaxKind.QstCollected ? movement.Amount : 0m);
                parameters.AddWithValue("qst_paid", movement.Kind == TaxKind.QstPaid ? movement.Amount : 0m);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
