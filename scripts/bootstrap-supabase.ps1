param([Parameter(Mandatory=$true)][string]$ProjectRef)
$ErrorActionPreference = "Stop"
if (-not (Test-Path .env)) { Copy-Item .env.example .env; Write-Host "Created .env. Fill it before starting Docker." }
npm install
npx supabase login
npx supabase link --project-ref $ProjectRef
npx supabase db push
Write-Host "Supabase schema and private buckets are ready. Now fill .env and run: docker compose up --build"
