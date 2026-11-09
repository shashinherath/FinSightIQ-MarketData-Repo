using Dapper;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FinSightIQ.MarketDataService.Tests.Integration;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("marketdb")
        .WithUsername("postgres")
        .WithPassword("postgres_dev_password")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public IDapperWrapper Dapper { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        DapperConfiguration.Configure();

        NpgsqlDataSourceBuilder builder = new NpgsqlDataSourceBuilder(ConnectionString);
        DataSource = builder.Build();

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MarketDb"] = ConnectionString
            })
            .Build();

        Dapper = new DapperWrapper(config);

        List<string> scriptPaths = FindDatabaseScripts();
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        foreach (string path in scriptPaths)
        {
            string sql = await File.ReadAllTextAsync(path);
            await conn.ExecuteAsync(sql);
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private static List<string> FindDatabaseScripts()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            string tableDir = Path.Combine(current, "Database", "Tables");
            string procDir = Path.Combine(current, "Database", "Procedures");
            if (Directory.Exists(tableDir) && Directory.Exists(procDir))
            {
                List<string> files = new List<string>();
                files.AddRange(Directory.GetFiles(tableDir, "*.sql").OrderBy(f => Path.GetFileName(f)));
                files.AddRange(Directory.GetFiles(procDir, "*.sql").OrderBy(f => Path.GetFileName(f)));

                if (files.Count > 0)
                {
                    return files;
                }
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new FileNotFoundException("Could not locate Database/Tables and Database/Procedures SQL scripts.");
    }
}

