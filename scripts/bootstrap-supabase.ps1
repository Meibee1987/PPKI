param([Parameter(Mandatory=$true)][string]$ProjectRef)
$ErrorActionPreference = "Stop"
if (-not (Test-Path .env)) { Copy-Item .env.example .env; Write-Host "Created .env. Fill it before starting Docker." }
npm.cmd ci
npm.cmd run supabase:login
npm.cmd run supabase:link -- --project-ref $ProjectRef
npm.cmd run supabase:push
Write-Host "Supabase schema and private buckets are ready. Now fill .env and run: npm run dev:up"
