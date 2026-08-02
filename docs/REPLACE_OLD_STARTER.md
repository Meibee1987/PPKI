# Replacing the previous local-Postgres starter

Use this Supabase starter as a new project folder. Do not copy its files one-by-one over a running old installation.

1. Stop the previous stack: `docker compose down`.
2. Keep the old folder as a backup.
3. Extract this starter into a new folder, for example `ppki-smart-formatter-supabase-starter`.
4. Copy only your own uncommitted rule/validator changes after comparing them.
5. Create `.env` from `.env.example`.
6. Link and push the Supabase migration.
7. Start the new stack with `docker compose up --build`.

The old local PostgreSQL volume and local document-storage volume are not used by this edition. Existing development data is not migrated automatically.
