using System.Data;
using Dapper;
using FinSightIQ.MarketDataService.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FinSightIQ.MarketDataService.Infrastructure.Persistence;

/// <summary>
/// Npgsql + Dapper execution wrapper for marketdb.
/// </summary>
public sealed class DapperWrapper : IDapperWrapper
{
    private readonly string _connectionString;

    static DapperWrapper()
    {
        DapperConfiguration.Configure();
    }

    public DapperWrapper(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _connectionString = configuration.GetConnectionString("MarketDb")
            ?? configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'MarketDb', 'Postgres', or 'DefaultConnection' was not found in configuration.");
    }

    public DapperWrapper(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public string ConnectionString => _connectionString;

    public async Task<IEnumerable<T>> QueryAsync<T>(
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        CommandDefinition command = new CommandDefinition(
            sql,
            param,
            transaction,
            commandTimeout,
            commandType,
            CommandFlags.Buffered,
            cancellationToken);

        return await connection.QueryAsync<T>(command);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        CommandDefinition command = new CommandDefinition(
            sql,
            param,
            transaction,
            commandTimeout,
            commandType,
            CommandFlags.Buffered,
            cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<T>(command);
    }

    public async Task<int> ExecuteAsync(
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        CommandDefinition command = new CommandDefinition(
            sql,
            param,
            transaction,
            commandTimeout,
            commandType,
            CommandFlags.Buffered,
            cancellationToken);

        return await connection.ExecuteAsync(command);
    }
}

