using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Postgres;

/// <summary>
/// Configures Jellyfin to use PostgreSQL via EF Core.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed class PostgresDatabaseProvider : IJellyfinDatabaseProvider
{
    private readonly ILogger<PostgresDatabaseProvider> _logger;

    public PostgresDatabaseProvider(ILogger<PostgresDatabaseProvider> logger)
    {
        _logger = logger;
    }

    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        var customOptions = databaseConfiguration.CustomProviderOptions
            ?? throw new InvalidOperationException("The PostgreSQL provider requires custom provider options.");

        if (string.IsNullOrWhiteSpace(customOptions.ConnectionString))
        {
            throw new InvalidOperationException("A PostgreSQL connection string must be supplied.");
        }

        var connectionBuilder = new NpgsqlConnectionStringBuilder(customOptions.ConnectionString);
        ApplyBuilderOverrides(connectionBuilder, customOptions.Options);

        var connectionString = connectionBuilder.ToString();

        options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName);
                var commandTimeout = GetOption<int?>(customOptions.Options, "command-timeout", value => (int?)int.Parse(value, CultureInfo.InvariantCulture));
                if (commandTimeout.HasValue)
                {
                    npgsqlOptions.CommandTimeout(commandTimeout.Value);
                }

                var retriesEnabled = GetOption<bool?>(customOptions.Options, "enable-retry-on-failure", value => (bool?)ParseBool(value));
                if (retriesEnabled.GetValueOrDefault())
                {
                    var retryCount = GetOption<int?>(customOptions.Options, "retry-count", value => (int?)int.Parse(value, CultureInfo.InvariantCulture)) ?? 5;
                    var retryDelay = GetOption<int?>(customOptions.Options, "retry-delay-seconds", value => (int?)int.Parse(value, CultureInfo.InvariantCulture)) ?? 15;
                    npgsqlOptions.EnableRetryOnFailure(retryCount, TimeSpan.FromSeconds(retryDelay), null);
                }

                var historySchema = GetOption(customOptions.Options, "default-schema", static value => value);
                if (!string.IsNullOrWhiteSpace(historySchema))
                {
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", historySchema);
                }
            });

        var sensitiveLogging = GetOption<bool?>(customOptions.Options, "EnableSensitiveDataLogging", value => (bool?)ParseBool(value));
        if (sensitiveLogging.GetValueOrDefault())
        {
            options.EnableSensitiveDataLogging();
            _logger.LogWarning("EnableSensitiveDataLogging is enabled on the PostgreSQL provider. Avoid using this in production.");
        }

        var safeBuilder = new NpgsqlConnectionStringBuilder(connectionBuilder.ConnectionString);
        if (!string.IsNullOrEmpty(safeBuilder.Password))
        {
            safeBuilder.Password = "********";
        }

        _logger.LogInformation(
            "Configured PostgreSQL connection: Host={Host}, Database={Database}, Username={Username}",
            safeBuilder.Host,
            safeBuilder.Database,
            safeBuilder.Username);
    }

    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            _logger.LogDebug("Skipping PostgreSQL optimization because the DbContext factory is not ready.");
            return Task.CompletedTask;
        }

        return RunMaintenanceCommandsAsync(
            connection => new[]
            {
                "VACUUM (ANALYZE);",
                $"REINDEX DATABASE {QuoteIdentifier(connection.Database)};"
            },
            cancellationToken);
    }

    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.SetDefaultDateTimeKind(DateTimeKind.Utc);
    }

    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL does not require any custom conventions at this time.
    }

    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        => throw new NotSupportedException("PostgreSQL backups must be handled outside of Jellyfin.");

    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
        => throw new NotSupportedException("PostgreSQL backups must be handled outside of Jellyfin.");

    public Task DeleteBackup(string key)
        => throw new NotSupportedException("PostgreSQL backups must be handled outside of Jellyfin.");

    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var identifiers = tableNames
            .Select(QuoteIdentifier)
            .ToArray();

        if (identifiers.Length == 0)
        {
            return;
        }

        var sql = $"TRUNCATE TABLE {string.Join(", ", identifiers)} RESTART IDENTITY CASCADE;";
        await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
    }

    private async Task RunMaintenanceCommandsAsync(Func<NpgsqlConnection, IEnumerable<string>> commandFactory, CancellationToken cancellationToken)
    {
        var factory = DbContextFactory ?? throw new InvalidOperationException("DbContextFactory is not set.");
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            foreach (var commandText in commandFactory(connection))
            {
                await using var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = commandText;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Executed maintenance command: {Command}", commandText);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void ApplyBuilderOverrides(NpgsqlConnectionStringBuilder builder, ICollection<CustomDatabaseOption>? options)
    {
        if (options is null)
        {
            return;
        }

        foreach (var option in options.Where(static o => o.Key.StartsWith("builder:", StringComparison.OrdinalIgnoreCase)))
        {
            var key = option.Key["builder:".Length..];
            builder[key] = option.Value;
        }
    }

    private static T? GetOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
    {
        if (options is null)
        {
            return defaultValue is null ? default : defaultValue();
        }

        var option = options.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            return defaultValue is null ? default : defaultValue();
        }

        return converter(option.Value);
    }

    private static bool ParseBool(string value)
        => value.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Table names cannot be null or empty", nameof(identifier));
        }

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
