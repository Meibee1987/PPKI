#!/usr/bin/env sh
set -eu
if [ "$#" -ne 1 ]; then echo "Usage: ./scripts/bootstrap-supabase.sh <project-ref>"; exit 1; fi
[ -f .env ] || cp .env.example .env
npm install
npx supabase login
npx supabase link --project-ref "$1"
npx supabase db push
echo "Supabase schema and private buckets are ready. Fill .env, then run docker compose up --build."
