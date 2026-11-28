using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Core.Persistence;

// Callers hold the physical CAS lease through metadata and blob operations.
internal sealed class CasReferenceIndex : IDisposable
{
    private const int ApplicationId = 0x434D5741;
    private readonly SqliteConnection connection;

    static CasReferenceIndex()
    {
        SQLitePCL.Batteries.Init();
    }

    internal CasReferenceIndex(ArtifactStoreFiles files, bool initialize = false)
    {
        string path = Path.Combine(files.Actions, "index.sqlite");
        foreach (string candidate in new[] { files.Actions, path, path + "-journal", path + "-wal", path + "-shm" })
        {
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("Action index path cannot be redirected.");
            }
        }

        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using (SqliteCommand synchronous = Command(static x => x.CommandText = "PRAGMA synchronous=FULL;"))
            {
                _ = synchronous.ExecuteNonQuery();
            }

            using SqliteCommand identity = Command(static x => x.CommandText = "PRAGMA application_id;");
            using SqliteCommand version = Command(static x => x.CommandText = "PRAGMA user_version;");
            if ((long)identity.ExecuteScalar()! == ApplicationId && (long)version.ExecuteScalar()! == 1)
            {
                return;
            }

            using SqliteCommand tables = Command(static x => x.CommandText = "SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' LIMIT 1;");
            if (!initialize || (long)identity.ExecuteScalar()! != 0 || (long)version.ExecuteScalar()! != 0 ||
                tables.ExecuteScalar() is not null)
            {
                throw new InvalidDataException("Unrecognized artifact CAS reference index.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand schema = Command(static x => x.CommandText = """
                CREATE TABLE action_results(
                    action_key TEXT PRIMARY KEY CHECK(length(action_key)=64),
                    key_policy INTEGER NOT NULL CHECK(key_policy IN (1,2)),
                    blob TEXT NOT NULL CHECK(length(blob)=64),
                    size INTEGER NOT NULL CHECK(size>=0)
                ) STRICT, WITHOUT ROWID;
                CREATE INDEX action_results_blob ON action_results(blob);
                CREATE TABLE artifact_operations(
                    operation_id TEXT PRIMARY KEY CHECK(length(operation_id)=32),
                    receipt_blob TEXT NOT NULL CHECK(length(receipt_blob)=64),
                    size INTEGER NOT NULL CHECK(size BETWEEN 16 AND 4194304),
                    pinned INTEGER NOT NULL CHECK(pinned IN (0,1))
                ) STRICT, WITHOUT ROWID;
                PRAGMA application_id=1129142081;
                PRAGMA user_version=1;
                """);
            schema.Transaction = transaction;
            _ = schema.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal CasActionResult? Find(string key, int policy)
    {
        using SqliteCommand sql = Command(static x => x.CommandText = "SELECT key_policy, blob, size FROM action_results WHERE action_key=$key;");
        _ = sql.Parameters.AddWithValue("$key", key);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }

        string digest = row.GetString(1);
        long size = row.GetInt64(2);
        return row.GetInt32(0) == policy && ArtifactStoreFiles.IsCanonicalHex(digest, 64) && size >= 0
            ? new(1, key, digest, size)
            : throw new InvalidDataException("Action Cache entry has an invalid identity policy or blob descriptor.");
    }

    internal void Publish(CasActionResult result, int policy) => Publish([result], policy, CancellationToken.None);

    internal void Publish(IReadOnlyList<CasActionResult> results, int policy, CancellationToken ct)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand sql = Command(static x => x.CommandText =
            "INSERT INTO action_results(action_key,key_policy,blob,size) VALUES($key,$policy,$blob,$size);");
        sql.Transaction = transaction;
        SqliteParameter key = sql.Parameters.Add("$key", SqliteType.Text);
        _ = sql.Parameters.AddWithValue("$policy", policy);
        SqliteParameter blob = sql.Parameters.Add("$blob", SqliteType.Text);
        SqliteParameter size = sql.Parameters.Add("$size", SqliteType.Integer);
        foreach (CasActionResult result in results)
        {
            ct.ThrowIfCancellationRequested();
            key.Value = result.ActionDigest;
            blob.Value = result.Digest;
            size.Value = result.Size;
            _ = sql.ExecuteNonQuery();
        }

        ct.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal void Drop(string key)
    {
        using SqliteCommand sql = Command(static x => x.CommandText = "DELETE FROM action_results WHERE action_key=$key;");
        _ = sql.Parameters.AddWithValue("$key", key);
        _ = sql.ExecuteNonQuery();
    }

    internal void AddRoots(ISet<string> reachable, CancellationToken ct)
    {
        using SqliteCommand sql = Command(static x => x.CommandText = "SELECT action_key,key_policy,blob,size FROM action_results;");
        using SqliteDataReader rows = sql.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            string blob = rows.GetString(2);
            if (!ArtifactStoreFiles.IsCanonicalHex(rows.GetString(0), 64) || rows.GetInt32(1) is not (1 or 2) ||
                !ArtifactStoreFiles.IsCanonicalHex(blob, 64) || rows.GetInt64(3) < 0)
            {
                throw new InvalidDataException("Invalid Action Cache retention reference.");
            }

            _ = reachable.Add(blob);
        }
    }

    internal readonly record struct ReceiptReference(string OperationId, string Blob, long Size, bool Pinned);

    internal ReceiptReference? FindReceipt(string operationId)
    {
        using SqliteCommand sql = Command(static x => x.CommandText =
            "SELECT operation_id, receipt_blob, size, pinned FROM artifact_operations WHERE operation_id=$id;");
        _ = sql.Parameters.AddWithValue("$id", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        return row.Read() ? ReadReceipt(row) : null;
    }

    internal IEnumerable<ReceiptReference> Receipts(CancellationToken ct)
    {
        using SqliteCommand sql = Command(static x => x.CommandText =
            "SELECT operation_id, receipt_blob, size, pinned FROM artifact_operations ORDER BY operation_id;");
        using SqliteDataReader row = sql.ExecuteReader();
        while (row.Read())
        {
            ct.ThrowIfCancellationRequested();
            yield return ReadReceipt(row);
        }
    }

    private static ReceiptReference ReadReceipt(SqliteDataReader row)
    {
        string id = row.GetString(0);
        string blob = row.GetString(1);
        long size = row.GetInt64(2);
        int pinned = row.GetInt32(3);
        return ArtifactStoreFiles.IsCanonicalHex(id, 32) && ArtifactStoreFiles.IsCanonicalHex(blob, 64) &&
            size is >= 16 and <= 4 * 1024 * 1024 && pinned is 0 or 1
            ? new(id, blob, size, pinned == 1)
            : throw new InvalidDataException("Invalid artifact manifest reference.");
    }

    internal void PublishReceipt(ReceiptReference receipt)
    {
        using SqliteCommand sql = Command(static x => x.CommandText =
            "INSERT INTO artifact_operations(operation_id,receipt_blob,size,pinned) VALUES($id,$blob,$size,$pinned);");
        _ = sql.Parameters.AddWithValue("$id", receipt.OperationId);
        _ = sql.Parameters.AddWithValue("$blob", receipt.Blob);
        _ = sql.Parameters.AddWithValue("$size", receipt.Size);
        _ = sql.Parameters.AddWithValue("$pinned", receipt.Pinned ? 1 : 0);
        _ = sql.ExecuteNonQuery();
    }

    internal void SetPinned(string operationId, bool pinned)
    {
        using SqliteCommand sql = Command(static x => x.CommandText =
            "UPDATE artifact_operations SET pinned=$pinned WHERE operation_id=$id;");
        _ = sql.Parameters.AddWithValue("$id", operationId);
        _ = sql.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        if (sql.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("Artifact manifest reference is missing.");
        }
    }

    private SqliteCommand Command(Action<SqliteCommand> configure)
    {
        SqliteCommand sql = connection.CreateCommand();
        configure(sql);
        return sql;
    }

    public void Dispose() => connection.Dispose();
}
