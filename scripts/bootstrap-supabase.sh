#!/usr/bin/env sh
set -eu
npm ci
npm run dev:infra
echo "Supabase lokal siap. Jalankan npm run dev:backend dan npm run dev:web dari terminal terpisah."
