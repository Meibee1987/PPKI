# Architecture — Supabase edition

```text
Browser / Next.js 16
  ├─ Supabase Auth (SSR cookies)
  └─ Bearer access token
             │
             ▼
ASP.NET Core API
  ├─ verifies token through Supabase Auth /user
  ├─ enforces document ownership
  ├─ EF Core → Supabase Postgres
  └─ Supabase Storage secret-key client
             │
             ▼
.NET Worker
  ├─ claims queued audit jobs
  ├─ downloads private DOCX to a temporary file
  ├─ Open XML parser
  ├─ PPKI rule engine
  └─ writes findings to Supabase Postgres
```

Supabase replaces local PostgreSQL, local file storage, and local identity. It does not replace the ASP.NET API, worker, Open XML parser, rule engine, or later fix engine.
