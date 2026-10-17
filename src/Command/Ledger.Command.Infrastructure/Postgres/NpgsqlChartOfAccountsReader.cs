using Ledger.Command.Application.Accounts;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Infrastructure.Accounts;
using Ledger.SharedKernel;
using Npgsql;

namespace Ledger.Command.Infrastructure.Postgres;

/// <summary>Plan comptable d'une entité, lu dans la table de référence <c>ledger.accounts</c>.</summary>
public sealed class NpgsqlChartOfAccountsReader(NpgsqlDataSource dataSource) : IChartOfAccountsReader
{
    private const string Sql =
        """
        SELECT account_id, currency, normal_side, is_active, no_negative_balance
        FROM ledger.accounts
        WHERE entity_id = @entity_id
        """;

    public async Task<IChartOfAccounts> LoadAsync(EntityId entity, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(Sql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("entity_id", entity.Value);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var accounts = new Dictionary<AccountId, AccountInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = new AccountId(reader.GetString(0));
            var currency = Currency.FromCode(reader.GetString(1));
            if (currency.IsFailure)
            {
                throw new InvalidOperationException($"Compte {entity}/{id} : {currency.Error.Message}");
            }

            var side = reader.GetString(2) == nameof(Side.Credit) ? Side.Credit : Side.Debit;
            accounts[id] = new AccountInfo(id, currency.Value, side, reader.GetBoolean(3), reader.GetBoolean(4));
        }

        return new ChartSnapshot(accounts);
    }
}
