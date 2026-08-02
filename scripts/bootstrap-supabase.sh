#!/usr/bin/env sh
set -eu
if [ "$#" -ne 1 ]; then echo "Usage: ./scripts/bootstrap-supabase.sh <project-ref>"; exit 1; fi
[ -f .env ] || cp .env.example .env
npm ci
npm run supabase:login
npm run supabase:link -- --project-ref "$1"
npm run supabase:push
echo "Supabase schema and private buckets are ready. Fill .env, then run npm run dev:up."
