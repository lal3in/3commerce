using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Allocates a storefront's next public order number with ONE atomic statement, in its own short
/// READ COMMITTED transaction on its own connection — never inside the caller's transaction.
/// <para>
/// Why not the consumer's transaction: the confirmation runs in the EF outbox's REPEATABLE READ
/// transaction, whose snapshot is taken at the inbox lock. Every order of a storefront shares one
/// counter row, so two confirmations in one storefront both read the same <c>NextNumber</c> and the
/// later one failed with <c>40001</c> (or <c>23505</c> on the storefront's first order, when both inserted
/// the row). No statement fixes that inside the snapshot: under REPEATABLE READ even
/// <c>INSERT … ON CONFLICT DO UPDATE</c> raises <c>40001</c> when the row changed after the snapshot.
/// Under READ COMMITTED the same upsert waits for a concurrent holder of the row lock and then
/// increments the latest committed value, so concurrent allocations queue for microseconds and never fail.
/// </para>
/// <para>
/// Consequence (accepted by the owner, 2026-10-11): the number is committed before the order is. A
/// confirmation that rolls back (a retry, a crash) burns its number, so a storefront's numbers can have
/// gaps. They are order REFERENCES, not tax-invoice numbers; uniqueness per storefront is still enforced
/// by the unique index on <c>(StorefrontId, PublicOrderNumber)</c>.
/// </para>
/// <para>
/// The connection comes from a pool of its own (a distinct <c>Application Name</c>), so a burst of
/// consumers that already hold every connection of the main pool cannot starve the allocation they are
/// each waiting for. Ordering's tables have no row-level security, so no tenant scope is needed here.
/// </para>
/// </summary>
public sealed class OrderNumberAllocator(OrderingDbContext db)
{
    /// <summary>Suffix that gives the allocator its own Npgsql pool (pools are keyed by connection string).</summary>
    public const string ApplicationNameSuffix = "order-numbers";

    private const string AllocateSql =
        """
        INSERT INTO ordering."OrderNumberSequences" ("StorefrontId", "NextNumber")
        VALUES (@storefront, @first + 1)
        ON CONFLICT ("StorefrontId") DO UPDATE
            SET "NextNumber" = ordering."OrderNumberSequences"."NextNumber" + 1
        RETURNING "NextNumber" - 1
        """;

    /// <summary>Reserves and commits the storefront's next order number (the first is
    /// <see cref="OrderNumberSequence.FirstNumber"/>).</summary>
    public async Task<long> NextAsync(Guid storefrontId, CancellationToken ct)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("OrderingDbContext has no connection string to allocate order numbers with.");

        await using var connection = new NpgsqlConnection(AllocatorConnectionString(connectionString));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = new NpgsqlCommand(AllocateSql, connection, transaction);
        command.Parameters.AddWithValue("storefront", storefrontId);
        command.Parameters.AddWithValue("first", OrderNumberSequence.FirstNumber);
        var number = (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Order number allocation returned no row."));
        await transaction.CommitAsync(ct);
        return number;
    }

    /// <summary>
    /// The service's connection string with the allocator's own <c>Application Name</c> (so its own pool)
    /// and no enlistment in an ambient <c>TransactionScope</c>: the allocation must commit on its own.
    /// </summary>
    public static string AllocatorConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false };
        builder.ApplicationName = string.IsNullOrEmpty(builder.ApplicationName)
            ? ApplicationNameSuffix
            : $"{builder.ApplicationName}/{ApplicationNameSuffix}";
        return builder.ConnectionString;
    }
}
