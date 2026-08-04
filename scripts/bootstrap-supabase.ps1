$ErrorActionPreference = "Stop"
npm.cmd ci
npm.cmd run dev:infra
Write-Host "Supabase lokal siap. Jalankan npm run dev:backend dan npm run dev:web dari terminal terpisah."
