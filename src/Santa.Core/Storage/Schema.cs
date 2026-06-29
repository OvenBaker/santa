namespace Santa.Core.Storage;

public static class Schema
{
    public const int Version = 1;

    public const string Sql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous  = NORMAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id                TEXT PRIMARY KEY,           -- session_id (UUID)
            project_path      TEXT NOT NULL,              -- ~/.claude/projects/<encoded> directory
            cwd               TEXT,                       -- decoded working directory
            git_branch        TEXT,                       -- recorded at session start
            derived_branches  TEXT,                       -- JSON array; branches inferred from git/gh tool calls
            started_at        TEXT,                       -- ISO8601
            ended_at          TEXT,
            message_count     INTEGER NOT NULL DEFAULT 0,
            turn_count        INTEGER NOT NULL DEFAULT 0,
            first_user_text   TEXT,                       -- truncated, for preview
            last_user_text    TEXT,
            status            TEXT NOT NULL DEFAULT 'active',  -- active|completed|archived
            status_source     TEXT,                       -- recipe id or 'manual'
            status_set_at     TEXT,
            provider          TEXT NOT NULL DEFAULT 'claude-code'  -- 'claude-code' | 'codex'
        );
        CREATE INDEX IF NOT EXISTS idx_sessions_status      ON sessions(status);
        CREATE INDEX IF NOT EXISTS idx_sessions_started_at  ON sessions(started_at);
        CREATE INDEX IF NOT EXISTS idx_sessions_cwd         ON sessions(cwd);
        CREATE INDEX IF NOT EXISTS idx_sessions_provider    ON sessions(provider);

        CREATE TABLE IF NOT EXISTS chunks (
            id            INTEGER PRIMARY KEY,
            session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            start_seq     INTEGER NOT NULL,
            end_seq       INTEGER NOT NULL,
            start_ts      TEXT,
            end_ts        TEXT,
            text          TEXT NOT NULL,
            approx_tokens INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_chunks_session ON chunks(session_id);

        -- Content-bearing FTS so snippet()/highlight() work. We pay 2× storage on
        -- chunk text; cheap given our scale.
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            text,
            session_id UNINDEXED,
            chunk_id   UNINDEXED
        );

        CREATE TABLE IF NOT EXISTS files (
            path        TEXT PRIMARY KEY,
            size        INTEGER NOT NULL,
            mtime_unix  INTEGER NOT NULL,
            last_offset INTEGER NOT NULL DEFAULT 0,
            last_seq    INTEGER NOT NULL DEFAULT -1,
            session_id  TEXT
        );

        CREATE TABLE IF NOT EXISTS classifications (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            recipe_id  TEXT NOT NULL,
            ts         TEXT NOT NULL,
            status     TEXT,
            evidence   TEXT,
            model      TEXT,
            raw_json   TEXT,
            PRIMARY KEY (session_id, recipe_id)
        );

        CREATE TABLE IF NOT EXISTS tags (
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            tag        TEXT NOT NULL,
            PRIMARY KEY (session_id, tag)
        );

        -- Embedding bookkeeping. The actual vectors live in the vec0 virtual
        -- table (chunk_vec) which is created separately, gated on the
        -- sqlite-vec extension being loaded. We track which model was used
        -- per chunk so a model swap is a clean re-embed.
        CREATE TABLE IF NOT EXISTS chunk_embeddings (
            chunk_id   INTEGER PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
            model_id   TEXT NOT NULL,
            dim        INTEGER NOT NULL,
            embedded_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_chunk_emb_model ON chunk_embeddings(model_id);
        """;

    public const string VecSchemaSqlTemplate = """
        CREATE VIRTUAL TABLE IF NOT EXISTS chunk_vec USING vec0(
            embedding float[{0}]
        );
        """;
}
