# Supabase setup from zero

1. Create one hosted Supabase project.
2. In **Authentication > URL Configuration**, set Site URL to `http://localhost:3000` and add `http://localhost:3000/auth/callback` as a redirect URL.
3. In **Project Settings > API**, copy Project URL, publishable key, and secret key.
4. In **Connect**, select **Session pooler** and copy its connection values into `SUPABASE_DB_CONNECTION`.
5. Copy `.env.example` to `.env` and fill all required values.
6. Apply the schema:

```powershell
npm install
npx supabase login
npx supabase link --project-ref YOUR_PROJECT_REF
npx supabase db push
```

7. Confirm these private buckets exist: `documents-original`, `documents-versions`, `audit-reports`.
8. Run the application:

```powershell
docker compose up --build
```

9. Open `http://localhost:3000`, sign up, upload a DOCX, and run an audit.

## Security model

- Browser: publishable key only.
- ASP.NET API and worker: secret key and Postgres connection string.
- DOCX files: private Storage buckets; the API creates short-lived signed URLs.
- Business authorization: API checks `owner_user_id` from the authenticated Supabase user.
- RLS remains enabled as defense in depth for any Data API access.
