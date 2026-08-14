using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

/// <summary>
/// Owns the relay's durable SQLite database and applies forward-only schema migrations.
/// A short-lived connection is opened for each store operation; connection pooling and WAL
/// make those reads cheap while keeping writers serialized by SQLite.
/// </summary>
public sealed class RelayDatabase
{
    public const int CurrentSchemaVersion = 7;
    private const int BusyTimeoutMilliseconds = 5_000;
    private readonly string connectionString;
    private readonly ILogger<RelayDatabase> logger;

    public RelayDatabase(ILogger<RelayDatabase> logger)
    {
        this.logger = logger;
        DataDirectory = ResolveDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "emotelink-relay.db");
        LegacyCommunityLabelsPath = Path.Combine(DataDirectory, "community-role-labels.json");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1_000
        }.ToString();
        Initialize();
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LegacyCommunityLabelsPath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ConfigureConnection(connection);
        return connection;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        // journal_mode cannot be changed inside a transaction. The remaining settings are
        // connection-local and are applied again by OpenConnection.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=5000;
                """;
            command.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var applied = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) applied.Add(reader.GetInt32(0));
        }

        if (applied.Any(version => version > CurrentSchemaVersion))
            throw new InvalidOperationException(
                $"Relay database schema {applied.Max()} is newer than this relay supports " +
                $"({CurrentSchemaVersion}); startup was stopped to protect the data.");

        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Version)) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();

            command.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_utc)
                VALUES ($version, $name, $appliedUtc);
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            logger.LogInformation("Applied relay database migration {Version}: {Name}",
                migration.Version, migration.Name);
        }

        transaction.Commit();
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;
        command.ExecuteNonQuery();
    }

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("EMOTELINK_DATA_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmoteLink.Relay")
            : Path.GetFullPath(configured);
    }

    private static readonly Migration[] Migrations =
    [
        new(1, "community role labels", """
            CREATE TABLE community_role_labels (
                record_key TEXT PRIMARY KEY COLLATE NOCASE,
                fingerprint TEXT NOT NULL COLLATE NOCASE,
                mod_name TEXT NOT NULL DEFAULT '',
                animation_name TEXT NOT NULL DEFAULT '',
                option_group TEXT NOT NULL,
                option_name TEXT NOT NULL,
                accepted_label TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX ix_community_role_labels_fingerprint
                ON community_role_labels(fingerprint COLLATE NOCASE);
            CREATE INDEX ix_community_role_labels_accepted
                ON community_role_labels(accepted_label COLLATE NOCASE);

            CREATE TABLE community_role_label_votes (
                record_key TEXT NOT NULL COLLATE NOCASE,
                reporter_hash TEXT NOT NULL COLLATE NOCASE,
                label TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (record_key, reporter_hash),
                FOREIGN KEY (record_key) REFERENCES community_role_labels(record_key) ON DELETE CASCADE
            );
            CREATE INDEX ix_community_role_label_votes_record_label
                ON community_role_label_votes(record_key, label COLLATE NOCASE);

            CREATE TABLE legacy_data_imports (
                import_key TEXT PRIMARY KEY,
                source_path TEXT NOT NULL,
                source_last_write_utc TEXT NOT NULL,
                imported_utc TEXT NOT NULL,
                record_count INTEGER NOT NULL
            );
            """),
        new(2, "animation artifact catalog", """
            CREATE TABLE animation_artifacts (
                signature TEXT PRIMARY KEY COLLATE NOCASE,
                signature_algorithm TEXT NOT NULL,
                manifest_file_count INTEGER NOT NULL DEFAULT 0 CHECK (manifest_file_count >= 0),
                manifest_bytes INTEGER NOT NULL DEFAULT 0 CHECK (manifest_bytes >= 0),
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );

            -- One content signature may be distributed under several harmless display names.
            -- Names are deliberately not associated with a user, path, room, or private/public flag.
            CREATE TABLE animation_artifact_names (
                signature TEXT NOT NULL COLLATE NOCASE,
                display_name TEXT NOT NULL COLLATE NOCASE,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (signature, display_name),
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE
            );
            CREATE INDEX ix_animation_artifact_names_name
                ON animation_artifact_names(display_name COLLATE NOCASE);

            CREATE TABLE animation_artifact_reports (
                signature TEXT NOT NULL COLLATE NOCASE,
                reporter_hash TEXT NOT NULL COLLATE NOCASE,
                classification INTEGER NOT NULL CHECK (classification IN (1, 2)),
                observed_utc TEXT NOT NULL,
                PRIMARY KEY (signature, reporter_hash),
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE
            );
            CREATE INDEX ix_animation_artifact_reports_signature_classification
                ON animation_artifact_reports(signature, classification);

            CREATE TABLE animation_artifact_consensus (
                signature TEXT PRIMARY KEY COLLATE NOCASE,
                classification INTEGER NOT NULL DEFAULT 0 CHECK (classification IN (0, 1, 2)),
                confidence REAL NOT NULL DEFAULT 0 CHECK (confidence >= 0 AND confidence <= 1),
                animation_reports INTEGER NOT NULL DEFAULT 0 CHECK (animation_reports >= 0),
                non_animation_reports INTEGER NOT NULL DEFAULT 0 CHECK (non_animation_reports >= 0),
                calculated_utc TEXT NOT NULL,
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE
            );
            CREATE INDEX ix_animation_artifact_consensus_classification
                ON animation_artifact_consensus(classification);

            CREATE TABLE animation_artifact_overrides (
                signature TEXT PRIMARY KEY COLLATE NOCASE,
                classification INTEGER NULL CHECK (classification IS NULL OR classification IN (1, 2)),
                sharing_policy INTEGER NOT NULL DEFAULT 0 CHECK (sharing_policy IN (0, 1, 2)),
                reason_code TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                created_by_hash TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                revoked_utc TEXT NULL,
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE
            );
            CREATE INDEX ix_animation_artifact_overrides_active
                ON animation_artifact_overrides(revoked_utc, sharing_policy);

            -- A verified, portable extraction result lets clients populate a known animation
            -- mod without opening its local manifest. JSON is schema-versioned and may contain
            -- normalized game paths/options only; it must never contain a local filesystem path.
            CREATE TABLE animation_artifact_payloads (
                signature TEXT NOT NULL COLLATE NOCASE,
                payload_schema_version INTEGER NOT NULL CHECK (payload_schema_version > 0),
                extractor_version TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL COLLATE NOCASE,
                payload_json TEXT NOT NULL,
                verification_reports INTEGER NOT NULL DEFAULT 1 CHECK (verification_reports > 0),
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (signature, payload_sha256),
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE
            );
            CREATE INDEX ix_animation_artifact_payloads_version
                ON animation_artifact_payloads(payload_schema_version, extractor_version);

            CREATE TABLE animation_artifact_payload_reports (
                signature TEXT NOT NULL COLLATE NOCASE,
                reporter_hash TEXT NOT NULL COLLATE NOCASE,
                payload_sha256 TEXT NOT NULL COLLATE NOCASE,
                observed_utc TEXT NOT NULL,
                PRIMARY KEY (signature, reporter_hash),
                FOREIGN KEY (signature) REFERENCES animation_artifacts(signature) ON DELETE CASCADE,
                FOREIGN KEY (signature, payload_sha256)
                    REFERENCES animation_artifact_payloads(signature, payload_sha256) ON DELETE CASCADE
            );
            """),
        new(3, "transfer moderation and audit", """
            CREATE TABLE transfer_sharing_bans (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope INTEGER NOT NULL CHECK (scope IN (1, 2, 3)),
                match_value TEXT NOT NULL COLLATE NOCASE,
                reason_code INTEGER NOT NULL CHECK (reason_code IN (1, 2, 3, 4, 5)),
                note TEXT NOT NULL DEFAULT '',
                created_by_hash TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                revoked_utc TEXT NULL,
                UNIQUE (scope, match_value)
            );
            CREATE INDEX ix_transfer_sharing_bans_active_match
                ON transfer_sharing_bans(scope, match_value COLLATE NOCASE, revoked_utc);

            -- This stores moderation metadata only. Package bytes remain in the existing
            -- ten-minute transfer store and are never copied into SQLite.
            CREATE TABLE moderation_audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                transfer_id TEXT NOT NULL DEFAULT '',
                event_type TEXT NOT NULL,
                occurred_utc TEXT NOT NULL,
                package_sha256 TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                catalog_fingerprint TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                mod_name_hash TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                note TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX ix_moderation_audit_events_occurred
                ON moderation_audit_events(occurred_utc DESC);
            CREATE INDEX ix_moderation_audit_events_transfer
                ON moderation_audit_events(transfer_id COLLATE NOCASE);
            """),
        new(4, "searchable transfer ban names", """
            ALTER TABLE transfer_sharing_bans
                ADD COLUMN display_name TEXT NOT NULL DEFAULT '';
            CREATE INDEX ix_transfer_sharing_bans_display_name
                ON transfer_sharing_bans(display_name COLLATE NOCASE);
            """),
        new(5, "trigram catalog search", """
            CREATE VIRTUAL TABLE animation_artifact_search USING fts5(
                artifact_key UNINDEXED,
                display_name,
                signature,
                tokenize='trigram'
            );
            INSERT INTO animation_artifact_search(artifact_key, display_name, signature)
                SELECT a.signature, COALESCE(n.display_name, ''), a.signature
                FROM animation_artifacts a
                LEFT JOIN animation_artifact_names n ON n.signature = a.signature;
            CREATE TRIGGER animation_artifacts_search_insert AFTER INSERT ON animation_artifacts BEGIN
                INSERT INTO animation_artifact_search(artifact_key, display_name, signature)
                    VALUES (new.signature, '', new.signature);
            END;
            CREATE TRIGGER animation_artifacts_search_delete AFTER DELETE ON animation_artifacts BEGIN
                DELETE FROM animation_artifact_search WHERE artifact_key = old.signature;
            END;
            CREATE TRIGGER animation_artifact_names_search_insert AFTER INSERT ON animation_artifact_names BEGIN
                INSERT INTO animation_artifact_search(artifact_key, display_name, signature)
                    VALUES (new.signature, new.display_name, new.signature);
            END;
            CREATE TRIGGER animation_artifact_names_search_delete AFTER DELETE ON animation_artifact_names BEGIN
                DELETE FROM animation_artifact_search
                    WHERE artifact_key = old.signature AND display_name = old.display_name;
            END;

            CREATE VIRTUAL TABLE community_label_search USING fts5(
                record_key UNINDEXED,
                display_name,
                animation_name,
                accepted_label,
                fingerprint,
                option_group,
                option_name,
                tokenize='trigram'
            );
            INSERT INTO community_label_search
                (record_key, display_name, animation_name, accepted_label,
                 fingerprint, option_group, option_name)
                SELECT record_key, mod_name, animation_name, accepted_label,
                       fingerprint, option_group, option_name
                FROM community_role_labels;
            CREATE TRIGGER community_labels_search_insert AFTER INSERT ON community_role_labels BEGIN
                INSERT INTO community_label_search
                    (record_key, display_name, animation_name, accepted_label,
                     fingerprint, option_group, option_name)
                    VALUES (new.record_key, new.mod_name, new.animation_name, new.accepted_label,
                            new.fingerprint, new.option_group, new.option_name);
            END;
            CREATE TRIGGER community_labels_search_update AFTER UPDATE ON community_role_labels BEGIN
                DELETE FROM community_label_search WHERE record_key = old.record_key;
                INSERT INTO community_label_search
                    (record_key, display_name, animation_name, accepted_label,
                     fingerprint, option_group, option_name)
                    VALUES (new.record_key, new.mod_name, new.animation_name, new.accepted_label,
                            new.fingerprint, new.option_group, new.option_name);
            END;
            CREATE TRIGGER community_labels_search_delete AFTER DELETE ON community_role_labels BEGIN
                DELETE FROM community_label_search WHERE record_key = old.record_key;
            END;

            CREATE VIRTUAL TABLE community_vote_search USING fts5(
                record_key UNINDEXED,
                reporter_hash UNINDEXED,
                label,
                tokenize='trigram'
            );
            INSERT INTO community_vote_search(record_key, reporter_hash, label)
                SELECT record_key, reporter_hash, label FROM community_role_label_votes;
            CREATE TRIGGER community_votes_search_insert AFTER INSERT ON community_role_label_votes BEGIN
                INSERT INTO community_vote_search(record_key, reporter_hash, label)
                    VALUES (new.record_key, new.reporter_hash, new.label);
            END;
            CREATE TRIGGER community_votes_search_update AFTER UPDATE ON community_role_label_votes BEGIN
                DELETE FROM community_vote_search
                    WHERE record_key = old.record_key AND reporter_hash = old.reporter_hash;
                INSERT INTO community_vote_search(record_key, reporter_hash, label)
                    VALUES (new.record_key, new.reporter_hash, new.label);
            END;
            CREATE TRIGGER community_votes_search_delete AFTER DELETE ON community_role_label_votes BEGIN
                DELETE FROM community_vote_search
                    WHERE record_key = old.record_key AND reporter_hash = old.reporter_hash;
            END;

            CREATE VIRTUAL TABLE transfer_ban_search USING fts5(
                ban_id UNINDEXED,
                display_name,
                note,
                match_value,
                tokenize='trigram'
            );
            INSERT INTO transfer_ban_search(ban_id, display_name, note, match_value)
                SELECT CAST(id AS TEXT), display_name, note, match_value FROM transfer_sharing_bans;
            CREATE TRIGGER transfer_bans_search_insert AFTER INSERT ON transfer_sharing_bans BEGIN
                INSERT INTO transfer_ban_search(ban_id, display_name, note, match_value)
                    VALUES (CAST(new.id AS TEXT), new.display_name, new.note, new.match_value);
            END;
            CREATE TRIGGER transfer_bans_search_update AFTER UPDATE ON transfer_sharing_bans BEGIN
                DELETE FROM transfer_ban_search WHERE ban_id = CAST(old.id AS TEXT);
                INSERT INTO transfer_ban_search(ban_id, display_name, note, match_value)
                    VALUES (CAST(new.id AS TEXT), new.display_name, new.note, new.match_value);
            END;
            CREATE TRIGGER transfer_bans_search_delete AFTER DELETE ON transfer_sharing_bans BEGIN
                DELETE FROM transfer_ban_search WHERE ban_id = CAST(old.id AS TEXT);
            END;
            """),
        new(6, "moderator payload approval and legacy reconciliation hashes", """
            ALTER TABLE animation_artifact_overrides
                ADD COLUMN approved_payload_sha256 TEXT NULL COLLATE NOCASE;
            CREATE INDEX ix_animation_artifact_overrides_approved_payload
                ON animation_artifact_overrides(signature, approved_payload_sha256, revoked_utc);

            ALTER TABLE legacy_data_imports
                ADD COLUMN source_sha256 TEXT NOT NULL DEFAULT '';
            ALTER TABLE legacy_data_imports
                ADD COLUMN sqlite_snapshot_sha256 TEXT NOT NULL DEFAULT '';
            ALTER TABLE legacy_data_imports
                ADD COLUMN sqlite_revision INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE legacy_data_imports
                ADD COLUMN mirrored_sqlite_revision INTEGER NOT NULL DEFAULT 0;

            CREATE TRIGGER legacy_import_revision_label_insert
            AFTER INSERT ON community_role_labels BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;
            CREATE TRIGGER legacy_import_revision_label_update
            AFTER UPDATE ON community_role_labels BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;
            CREATE TRIGGER legacy_import_revision_label_delete
            AFTER DELETE ON community_role_labels BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;
            CREATE TRIGGER legacy_import_revision_vote_insert
            AFTER INSERT ON community_role_label_votes BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;
            CREATE TRIGGER legacy_import_revision_vote_update
            AFTER UPDATE ON community_role_label_votes BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;
            CREATE TRIGGER legacy_import_revision_vote_delete
            AFTER DELETE ON community_role_label_votes BEGIN
                UPDATE legacy_data_imports SET sqlite_revision = sqlite_revision + 1
                WHERE import_key = 'community-role-labels-json-v1';
            END;

            CREATE TABLE animation_catalog_storage_stats (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                artifact_count INTEGER NOT NULL CHECK (artifact_count >= 0),
                payload_bytes INTEGER NOT NULL CHECK (payload_bytes >= 0)
            );
            INSERT INTO animation_catalog_storage_stats(singleton, artifact_count, payload_bytes)
                SELECT 1,
                       (SELECT COUNT(*) FROM animation_artifacts),
                       COALESCE((SELECT SUM(length(CAST(payload_json AS BLOB)))
                                 FROM animation_artifact_payloads), 0);

            CREATE TRIGGER animation_catalog_stats_artifact_insert
            AFTER INSERT ON animation_artifacts BEGIN
                UPDATE animation_catalog_storage_stats
                SET artifact_count = artifact_count + 1 WHERE singleton = 1;
            END;
            CREATE TRIGGER animation_catalog_stats_artifact_delete
            AFTER DELETE ON animation_artifacts BEGIN
                UPDATE animation_catalog_storage_stats
                SET artifact_count = artifact_count - 1 WHERE singleton = 1;
            END;
            CREATE TRIGGER animation_catalog_stats_payload_insert
            AFTER INSERT ON animation_artifact_payloads BEGIN
                UPDATE animation_catalog_storage_stats
                SET payload_bytes = payload_bytes + length(CAST(new.payload_json AS BLOB))
                WHERE singleton = 1;
            END;
            CREATE TRIGGER animation_catalog_stats_payload_delete
            AFTER DELETE ON animation_artifact_payloads BEGIN
                UPDATE animation_catalog_storage_stats
                SET payload_bytes = payload_bytes - length(CAST(old.payload_json AS BLOB))
                WHERE singleton = 1;
            END;
            CREATE TRIGGER animation_catalog_stats_payload_update
            AFTER UPDATE OF payload_json ON animation_artifact_payloads BEGIN
                UPDATE animation_catalog_storage_stats
                SET payload_bytes = payload_bytes
                    - length(CAST(old.payload_json AS BLOB))
                    + length(CAST(new.payload_json AS BLOB))
                WHERE singleton = 1;
            END;
            """),
        new(7, "anonymous relay statistics", """
            -- Only relay-wide counters are stored here. There are deliberately no user,
            -- connection, installation, character, room, or animation identifier columns.
            CREATE TABLE relay_statistics (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                rooms_generated INTEGER NOT NULL DEFAULT 0 CHECK (rooms_generated >= 0),
                shared_animations INTEGER NOT NULL DEFAULT 0 CHECK (shared_animations >= 0),
                animations_performed INTEGER NOT NULL DEFAULT 0 CHECK (animations_performed >= 0)
            );
            INSERT INTO relay_statistics(
                singleton, rooms_generated, shared_animations, animations_performed)
            VALUES (1, 0, 0, 0);
            """)
    ];

    private sealed record Migration(int Version, string Name, string Sql);
}
