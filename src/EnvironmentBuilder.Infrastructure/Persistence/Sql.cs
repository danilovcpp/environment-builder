using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EnvironmentBuilder.Infrastructure.Persistence;

internal static class Sql
{
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string text, params (string Key, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach ((string key, object? value) in parameters) { command.Parameters.AddWithValue(key, value ?? DBNull.Value); }
        return command;
    }
    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string text, params (string Key, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, text, parameters);
        return command.ExecuteNonQuery();
    }
    internal static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
