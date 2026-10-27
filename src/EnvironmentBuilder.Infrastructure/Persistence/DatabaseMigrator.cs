using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

public static class DatabaseMigrator
{
    public static string Initialize(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        string native = (string)command.ExecuteScalar()!;
        if (!Version.TryParse(native, out Version? version) || version < new Version(3, 51, 3))
        { throw new NotSupportedException("Native SQLite 3.51.3 or newer is required; actual: " + native); }
        command.CommandText = "PRAGMA user_version";
        long schema = (long)command.ExecuteScalar()!;
        if (schema is not 0 and not 1 and not 2) { throw new NotSupportedException("Unsupported database schema version: " + schema); }
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(command.ExecuteScalar(), "ok")) { throw new IOException("SQLite integrity check failed."); }
        if (schema == 0)
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if ((long)command.ExecuteScalar()! != 0) { throw new IOException("Unrecognized nonempty database; automatic replacement is forbidden."); }
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            using Stream stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream("EnvironmentBuilder.Infrastructure.Persistence.Migrations.001_Initial.sql")!;
            using var reader = new StreamReader(stream);
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO AppMetadata VALUES ('installationId',$id),('databaseSchemaVersion','1')";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.ExecuteNonQuery();
            transaction.Commit();
            command.Transaction = null;
        }
        if (schema < 2)
        {
            using SqliteTransaction migration = connection.BeginTransaction();
            command.Transaction = migration;
            using Stream stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream("EnvironmentBuilder.Infrastructure.Persistence.Migrations.002_InitializationAttempts.sql")!;
            using var reader = new StreamReader(stream);
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null;
        }
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return native;
    }
}
