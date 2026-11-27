using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace KozosRajzolo.Data;

/// <summary>
/// Indításkor lefuttatja az EF Core migrációkat. A régebbi, EnsureCreated-del
/// létrehozott adatbázisokat az első (InitialCreate) migrációra „rögzíti”,
/// így a meglévő adatok megmaradnak, és csak az újabb migrációk futnak le.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task MigrateAsync(DrawingDbContext db, ILogger logger)
    {
        if (db.Database.IsRelational() && await db.Database.CanConnectAsync())
            await BaselineLegacyDatabaseAsync(db, logger);
        await db.Database.MigrateAsync();
    }

    private static async Task BaselineLegacyDatabaseAsync(DrawingDbContext db, ILogger logger)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            var hasUsers = await TableExistsAsync(connection, "AspNetUsers");
            var hasHistory = await TableExistsAsync(connection, HistoryRepository.DefaultTableName);
            if (!hasUsers || hasHistory) return;

            var initial = db.Database.GetMigrations().First();
            var productVersion = typeof(DbContext).Assembly
                .GetName().Version?.ToString(3) ?? "9.0.0";
            logger.LogWarning("Régi (EnsureCreated) adatbázis észlelve, rögzítés a(z) {Migration} migrációra.", initial);

            await ExecuteAsync(connection,
                $"CREATE TABLE `{HistoryRepository.DefaultTableName}` (" +
                "`MigrationId` varchar(150) NOT NULL, `ProductVersion` varchar(32) NOT NULL, " +
                "PRIMARY KEY (`MigrationId`)) CHARACTER SET=utf8mb4;");
            await ExecuteAsync(connection,
                $"INSERT INTO `{HistoryRepository.DefaultTableName}` (`MigrationId`, `ProductVersion`) VALUES (@id, @version);",
                ("@id", initial), ("@version", productVersion));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name;";
        AddParameter(command, "@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) AddParameter(command, name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
