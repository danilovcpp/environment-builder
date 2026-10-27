using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EnvironmentBuilder.Demo;

public static class DemoHost
{
    public static async Task RunAsync(string[] args, string service, string? input, string? output)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
        string root = Environment.GetEnvironmentVariable("DATA_DIRECTORY") ?? "/data";
        Directory.CreateDirectory(root);
        builder.Services.AddSingleton(new DemoStore(Path.Combine(root, "state.db"), service, output));
        builder.Services.AddSingleton(p => new BrokerWorker(p.GetRequiredService<DemoStore>(), service, input));
        builder.Services.AddHostedService(p => p.GetRequiredService<BrokerWorker>());
        WebApplication app = builder.Build();
        app.MapGet("/health", (BrokerWorker broker) => broker.Connected ? Results.Ok(new { ready = true }) : Results.StatusCode(503));
        app.MapGet("/counts", (DemoStore store, BrokerWorker broker) => Results.Ok(new { records = store.Count(), initialized = store.Initialized(), broker = broker.Connected, pendingEvents = store.PendingCount() }));
        app.MapGet("/probe", (DemoStore store) => Results.Ok(new { service, records = store.Count(), digest = store.Digest() }));
        app.MapPost("/bootstrap", (HttpRequest request, DemoStore store) => Apply(request, store, "bootstrap", (connection, transaction) =>
        { DemoStore.Execute(connection, transaction, "INSERT OR REPLACE INTO Metadata VALUES('initialized','true')"); }));
        app.MapPost("/seed", async (HttpRequest request, DemoStore store, CancellationToken token) =>
        {
            string key = Key(request);
            if (key.Length == 0) { return Results.BadRequest(); }
            if (store.Applied(key, "seed")) { return Results.NoContent(); }
            // The private temporary file bounds memory while preserving an atomic seed+dedup commit.
            string temporary = Path.Combine(root, "seed-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                { await request.Body.CopyToAsync(file, token); }
                using var lines = new StreamReader(temporary, new UTF8Encoding(false, true));
                store.Mutate(key, "seed", (connection, transaction) =>
                {
                    string? line;
                    while ((line = lines.ReadLine()) is not null)
                    {
                        if (Encoding.UTF8.GetByteCount(line) > 65536) { throw new InvalidDataException("Record exceeds limit."); }
                        using JsonDocument record = JsonDocument.Parse(line);
                        string id = record.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("Record ID required.");
                        DemoStore.Execute(connection, transaction, "INSERT OR REPLACE INTO Records VALUES($id,$json)", ("$id", id), ("$json", record.RootElement.GetRawText()));
                    }
                });
                return Results.NoContent();
            }
            finally { File.Delete(temporary); }
        });
        app.MapPost("/business", async (HttpRequest request, DemoStore store, CancellationToken token) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
            string id = body.RootElement.GetProperty("id").GetString() ?? "";
            if (id.Length is 0 or > 256) { return Results.BadRequest(); }
            string payload = body.RootElement.GetRawText();
            return Apply(request, store, "business", (connection, transaction) => store.Record(connection, transaction, id, payload));
        });
        await app.RunAsync();
    }
    private static IResult Apply(HttpRequest request, DemoStore store, string action, Action<SqliteConnection, SqliteTransaction> effect)
    {
        string key = Key(request);
        if (key.Length == 0) { return Results.BadRequest(); }
        store.Mutate(key, action, effect);
        return Results.NoContent();
    }
    private static string Key(HttpRequest request)
    { string value = request.Headers["Idempotency-Key"].ToString(); return value.Length is > 0 and <= 256 && !value.Any(char.IsControl) ? value : ""; }
}

public sealed class DemoStore
{
    private readonly string connectionString;
    private readonly string service;
    private readonly string? output;
    private readonly object sync = new();
    public DemoStore(string path, string service, string? output)
    {
        this.service = service; this.output = output;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using SqliteConnection connection = Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Records(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Applied(key TEXT PRIMARY KEY,action TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Outbox(id TEXT PRIMARY KEY,route TEXT NOT NULL,json TEXT NOT NULL)");
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); Execute(connection, null, "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000"); return connection; }
    public bool Applied(string key, string action)
    {
        lock (sync)
        {
            using SqliteConnection connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT action FROM Applied WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
            object? existing = command.ExecuteScalar();
            if (existing is string previous && previous != action) { throw new InvalidOperationException("Idempotency key belongs to a different action."); }
            return existing is not null;
        }
    }
    public void Mutate(string key, string action, Action<SqliteConnection, SqliteTransaction> effect)
    {
        lock (sync)
        {
            if (Applied(key, action)) { return; }
            using SqliteConnection connection = Open(); using SqliteTransaction transaction = connection.BeginTransaction();
            effect(connection, transaction);
            Execute(connection, transaction, "INSERT INTO Applied VALUES($key,$action)", ("$key", key), ("$action", action));
            transaction.Commit();
        }
    }
    public void Record(SqliteConnection connection, SqliteTransaction transaction, string id, string json)
    {
        Execute(connection, transaction, "INSERT OR REPLACE INTO Records VALUES($id,$json)", ("$id", id), ("$json", json));
        if (output is not null) { Execute(connection, transaction, "INSERT OR IGNORE INTO Outbox VALUES($id,$route,$json)", ("$id", service + ":" + id), ("$route", output), ("$json", json)); }
    }
    public long Count() => Scalar("SELECT count(*) FROM Records");
    public long PendingCount() => Scalar("SELECT count(*) FROM Outbox");
    public bool Initialized() => Scalar("SELECT count(*) FROM Metadata WHERE key='initialized' AND value='true'") == 1;
    private long Scalar(string sql) { lock (sync) { using SqliteConnection connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; } }
    public string Digest()
    {
        lock (sync)
        {
            using SqliteConnection connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM Records ORDER BY id";
            using var rows = command.ExecuteReader(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (rows.Read()) { hash.AppendData(Encoding.UTF8.GetBytes(rows.GetString(0) + "\n")); }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
    public (string Id, string Route, string Json)? NextEvent()
    {
        lock (sync) { using SqliteConnection connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT id,route,json FROM Outbox ORDER BY id LIMIT 1"; using var row = command.ExecuteReader(); return row.Read() ? (row.GetString(0), row.GetString(1), row.GetString(2)) : null; }
    }
    public void Published(string id) { lock (sync) { using SqliteConnection connection = Open(); Execute(connection, null, "DELETE FROM Outbox WHERE id=$id", ("$id", id)); } }
    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach ((string name, object value) in parameters) { command.Parameters.AddWithValue(name, value); } command.ExecuteNonQuery(); }
}

public sealed class BrokerWorker(DemoStore store, string service, string? input) : BackgroundService
{
    private volatile bool connected;
    public bool Connected => connected;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = Environment.GetEnvironmentVariable("BROKER_HOST") ?? "localhost",
                    UserName = Environment.GetEnvironmentVariable("BROKER_USER") ?? "demo",
                    Password = Environment.GetEnvironmentVariable("BROKER_PASSWORD") ?? "demo",
                    AutomaticRecoveryEnabled = false
                };
                await using IConnection connection = await factory.CreateConnectionAsync(stoppingToken);
                await using IChannel publishing = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken);
                await using IChannel consuming = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await publishing.ExchangeDeclareAsync("demo", ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
                // Declare both durable routes before publishing so consumer startup order cannot discard events.
                foreach ((string queue, string route) in new[] { ("scheduler", "requests.created"), ("reporting", "scheduler.scheduled") })
                {
                    await publishing.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                    await publishing.QueueBindAsync(queue, "demo", route, cancellationToken: stoppingToken);
                }
                if (input is not null)
                {
                    await consuming.BasicQosAsync(0, 10, false, stoppingToken);
                    var consumer = new AsyncEventingBasicConsumer(consuming);
                    consumer.ReceivedAsync += async (_, delivery) =>
                    {
                        string json = Encoding.UTF8.GetString(delivery.Body.Span);
                        using JsonDocument record = JsonDocument.Parse(json);
                        string id = record.RootElement.GetProperty("id").GetString()!;
                        store.Mutate("event:" + delivery.BasicProperties.MessageId, "consume", (db, tx) => store.Record(db, tx, id, json));
                        await consuming.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        Console.WriteLine("event applied");
                    };
                    await consuming.BasicConsumeAsync(service, false, consumer, stoppingToken);
                }
                connected = true;
                while (!stoppingToken.IsCancellationRequested && connection.IsOpen && publishing.IsOpen && consuming.IsOpen)
                {
                    if (store.NextEvent() is { } message)
                    {
                        await publishing.BasicPublishAsync("demo", message.Route, true, new BasicProperties { MessageId = message.Id, Persistent = true, ContentType = "application/json" }, Encoding.UTF8.GetBytes(message.Json), stoppingToken);
                        store.Published(message.Id);
                        Console.WriteLine("event published");
                    }
                    else { await Task.Delay(200, stoppingToken); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { Console.WriteLine("broker unavailable; durable outbox retained"); }
            finally { connected = false; }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
