using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Storage.Documents;

internal sealed class Catalog : IDisposable
{
    internal const int PrototypeVersion = 1;
    internal const int WorkflowVersion = 2;
    internal const int ExecutionVersion = 3;
    internal const int ReportVersion = 4;
    internal const int CollectProduction = 0;
    private const string Columns = "id, codec, schema_version, blob, canonical, stored_length";
    private readonly SqliteConnection connection;

    static Catalog()
    {
        SQLitePCL.Batteries.Init();
    }

    internal Catalog(string path, int expectedVersion, bool initialize = false)
    {
        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            _ = Execute("PRAGMA synchronous=FULL;");
            CheckVersion(expectedVersion, initialize);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void CheckVersion(int expectedVersion, bool initialize)
    {
        using SqliteCommand version = Command("PRAGMA user_version;");
        using SqliteCommand appId = Command("PRAGMA application_id;");
        long schema = (long)(version.ExecuteScalar() ?? -1L);
        long code = (long)(appId.ExecuteScalar() ?? -1L);
        bool newCatalog = schema == 0 && code == 0 && initialize;
        bool supported = expectedVersion == CollectProduction
            ? schema is WorkflowVersion or ExecutionVersion or ReportVersion : schema == expectedVersion;
        if (!newCatalog && (!supported || code != 0x434d5744))
        {
            throw new InvalidDataException("Unrecognized document catalog application or schema version.");
        }

        if (!initialize)
        {
            return;
        }

        if (newCatalog && Exists("SELECT 1 FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"))
        {
            throw new InvalidDataException("Document catalog path belongs to another database.");
        }

        using SqliteCommand journal = Command("PRAGMA journal_mode=WAL;");
        if (!string.Equals(journal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Document catalog requires SQLite WAL.");
        }

        if (newCatalog)
        {
            CreateSchema(expectedVersion);
        }
    }

    private void CreateSchema(int version)
    {
        string codecs = version switch
        {
            ReportVersion => "1,2,3,4,5",
            ExecutionVersion => "1,2,3,4",
            WorkflowVersion => "1,2,3",
            _ => "1,2"
        };
        string production = version >= WorkflowVersion
            ? "OR (codec=3 AND schema_version IN (1,2) AND canonical IS NOT NULL)" : "";
        string plans = version >= ExecutionVersion
            ? "OR (codec=4 AND schema_version=1 AND canonical IS NOT NULL)" : "";
        string reports = version >= ReportVersion
            ? "OR (codec=5 AND schema_version=1 AND canonical IS NOT NULL)" : "";
        using SqliteTransaction transaction = connection.BeginTransaction();
        _ = Execute($"""
            CREATE TABLE documents (
                id TEXT PRIMARY KEY NOT NULL CHECK(length(id)=64),
                codec INTEGER NOT NULL CHECK(codec IN ({codecs})),
                schema_version INTEGER NOT NULL,
                blob TEXT NOT NULL CHECK(length(blob)=64),
                canonical TEXT CHECK(canonical IS NULL OR length(canonical)=64),
                stored_length INTEGER NOT NULL CHECK(stored_length BETWEEN 1 AND 16777224),
                CHECK((codec=1 AND schema_version IN (1,2) AND canonical IS NULL)
                    OR (codec=2 AND schema_version=2 AND canonical IS NOT NULL) {production} {plans} {reports})
            ) STRICT;
            CREATE INDEX documents_by_blob ON documents(blob);
            CREATE TABLE retention (
                role INTEGER NOT NULL CHECK(role IN (1,2)),
                name TEXT NOT NULL,
                document TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
                PRIMARY KEY(role,name)
            ) STRICT;
            CREATE INDEX retention_by_document ON retention(document);
            CREATE TABLE equivalence (
                legacy TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                binary TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                projection TEXT NOT NULL CHECK(length(projection)=64),
                PRIMARY KEY(legacy,binary)
            ) STRICT;
            PRAGMA application_id=1129142084;
            """, transaction);
        _ = Execute(FormattableString.Invariant($"PRAGMA user_version={version};"), transaction);
        transaction.Commit();
    }

    internal SqliteTransaction Begin() => connection.BeginTransaction();

    internal DocumentDescriptor? Find(DocumentId id, SqliteTransaction? tx = null)
    {
        using SqliteCommand command = Command($"SELECT {Columns} FROM documents WHERE id=$id;", tx, ("$id", id.Value));
        using SqliteDataReader row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }

        try
        {
            DocumentDescriptor result = DocumentDescriptor.Create((DocumentCodec)row.GetInt32(1), row.GetInt32(2),
                new(row.GetString(3)), row.IsDBNull(4) ? null : new CanonicalDigest(row.GetString(4)), row.GetInt64(5));
            return result.Id == id && row.GetString(0) == id.Value
                ? result
                : throw new InvalidDataException("Catalog logical identity is inconsistent.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Malformed catalog identity.", exception);
        }
    }

    internal void Insert(DocumentDescriptor descriptor, SqliteTransaction tx)
    {
        DocumentDescriptor? previous = Find(descriptor.Id, tx);
        if (previous is not null)
        {
            if (previous != descriptor)
            {
                throw new InvalidDataException("Conflicting descriptor for an existing document identity.");
            }

            return;
        }

        _ = Execute("""
            INSERT INTO documents (id,codec,schema_version,blob,canonical,stored_length)
            VALUES($id,$codec,$schema,$blob,$canonical,$length);
            """, tx, ("$id", descriptor.Id.Value), ("$codec", (int)descriptor.Codec),
            ("$schema", descriptor.SchemaVersion), ("$blob", descriptor.Blob.Value),
            ("$canonical", descriptor.Canonical?.Value), ("$length", descriptor.StoredLength));
    }

    internal void Retain(int role, string name, DocumentId id, SqliteTransaction? tx = null) =>
        _ = Execute("""
            INSERT INTO retention(role,name,document) VALUES($role,$name,$id)
            ON CONFLICT(role,name) DO UPDATE SET document=excluded.document;
            """, tx, ("$role", role), ("$name", name), ("$id", id.Value));

    internal void Archive(string name, DocumentId id, SqliteTransaction tx)
    {
        _ = Execute("""
            INSERT INTO retention(role,name,document) VALUES(1,$name,$id) ON CONFLICT(role,name) DO NOTHING;
            """, tx, ("$name", name), ("$id", id.Value));
        if (!ExistsInTransaction("SELECT 1 FROM retention WHERE role=1 AND name=$name AND document=$id;", tx,
            ("$name", name), ("$id", id.Value)))
        {
            throw new InvalidDataException("An archive reference already names a different document.");
        }
    }

    private bool ExistsInTransaction(string sql, SqliteTransaction tx, params (string Name, object? Value)[] values)
    {
        using SqliteCommand query = Command(sql, tx, values);
        return query.ExecuteScalar() is not null;
    }

    internal int Release(int role, string name, SqliteTransaction tx) =>
        Execute("DELETE FROM retention WHERE role=$role AND name=$name;", tx, ("$role", role), ("$name", name));

    internal DocumentDescriptor? Resolve(int role, string name)
    {
        using SqliteCommand command = Command("SELECT document FROM retention WHERE role=$role AND name=$name;", null,
            ("$role", role), ("$name", name));
        return command.ExecuteScalar() is string id ? Find(new(id)) : null;
    }

    internal void Record(MigrationResult result, SqliteTransaction tx)
    {
        _ = Execute("""
            INSERT INTO equivalence (legacy,binary,projection) VALUES($old,$new,$hash)
            ON CONFLICT(legacy,binary) DO NOTHING;
            """, tx, ("$old", result.Legacy.Id.Value), ("$new", result.Binary.Id.Value),
            ("$hash", result.NormalizedProjectionSha256));
        if (Equivalence(result.Legacy.Id, result.Binary.Id, tx) != result.NormalizedProjectionSha256)
        {
            throw new InvalidDataException("Conflicting migration equivalence.");
        }
    }

    internal string? Equivalence(DocumentId legacy, DocumentId binary, SqliteTransaction? tx = null)
    {
        using SqliteCommand query = Command("SELECT projection FROM equivalence WHERE legacy=$old AND binary=$new;",
            tx, ("$old", legacy.Value), ("$new", binary.Value));
        string? digest = query.ExecuteScalar() as string;
        return digest is null || (ArtifactStoreFiles.IsCanonicalHex(digest, 64)
            && Find(legacy, tx)?.Codec == DocumentCodec.WorkflowJson
            && Find(binary, tx)?.Codec is DocumentCodec.WorkflowCapnp or DocumentCodec.ProductionWorkflowCapnp)
            ? digest
            : throw new InvalidDataException("Invalid catalog migration equivalence.");
    }

    internal bool IsRetained(string blob) => Exists("""
        SELECT 1 FROM documents d JOIN retention r ON r.document=d.id WHERE d.blob=$blob;
        """, ("$blob", blob));

    internal int Prune(string blob, SqliteTransaction tx) =>
        Execute("DELETE FROM documents WHERE blob=$blob;", tx, ("$blob", blob));

    internal int Delete(DocumentId id, SqliteTransaction tx) => Execute("""
        DELETE FROM documents WHERE id=$id AND NOT EXISTS(SELECT 1 FROM retention WHERE document=$id);
        """, tx, ("$id", id.Value));

    private bool Exists(string sql, params (string Name, object? Value)[] values)
    {
        using SqliteCommand command = Command(sql, null, values);
        return command.ExecuteScalar() is not null;
    }

    private int Execute(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] values)
    {
        using SqliteCommand command = Command(sql, tx, values);
        return command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100", Justification =
        "All SQL text comes from fixed statements in Catalog. Identifiers and values flow through named parameters.")]
    private SqliteCommand Command(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] values)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach ((string name, object? value) in values)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public void Dispose() => connection.Dispose();
}
