using EmailToDiscord.Configuration;
using Microsoft.Data.Sqlite;

namespace EmailToDiscord.Services;

public record ThreadRecord(
    ulong DiscordThreadId,
    string CustomerEmail,
    string OriginalSubject,
    string MailboxEmail,
    string? LatestInboundMsgId,
    bool AwaitingCustomer
);

public class ThreadStore
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public ThreadStore(AppConfig config)
    {
        var path = config.State.ThreadDbPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
        }.ToString();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS threads (
                discord_thread_id     INTEGER PRIMARY KEY,
                customer_email        TEXT    NOT NULL,
                original_subject      TEXT    NOT NULL,
                mailbox_email         TEXT    NOT NULL,
                latest_inbound_msgid  TEXT    NULL,
                awaiting_customer     INTEGER NOT NULL DEFAULT 0
            );
        ";
        cmd.ExecuteNonQuery();

        try
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE threads ADD COLUMN awaiting_customer INTEGER NOT NULL DEFAULT 0;";
            migrate.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists on pre-existing DBs.
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void RecordInbound(
        ulong discordThreadId,
        string customerEmail,
        string originalSubject,
        string mailboxEmail,
        string? inboundMsgId
    )
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO threads (discord_thread_id, customer_email, original_subject, mailbox_email, latest_inbound_msgid, awaiting_customer)
                VALUES ($tid, $email, $subj, $mbox, $msgid, 0)
                ON CONFLICT(discord_thread_id) DO UPDATE SET
                    latest_inbound_msgid = COALESCE(excluded.latest_inbound_msgid, threads.latest_inbound_msgid),
                    awaiting_customer = 0;
            ";
            cmd.Parameters.AddWithValue("$tid", (long)discordThreadId);
            cmd.Parameters.AddWithValue("$email", customerEmail);
            cmd.Parameters.AddWithValue("$subj", originalSubject ?? "");
            cmd.Parameters.AddWithValue("$mbox", mailboxEmail);
            cmd.Parameters.AddWithValue("$msgid", (object?)inboundMsgId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public ThreadRecord? Get(ulong discordThreadId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT customer_email, original_subject, mailbox_email, latest_inbound_msgid, awaiting_customer
                FROM threads
                WHERE discord_thread_id = $tid;
            ";
            cmd.Parameters.AddWithValue("$tid", (long)discordThreadId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new ThreadRecord(
                discordThreadId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0
            );
        }
    }

    public void RecordOutbound(ulong discordThreadId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE threads
                SET awaiting_customer = 1
                WHERE discord_thread_id = $tid;
            ";
            cmd.Parameters.AddWithValue("$tid", (long)discordThreadId);
            cmd.ExecuteNonQuery();
        }
    }
}
