import { createBrowserClient } from "@supabase/ssr";
import { getPublicSupabaseEnvironment } from "./environment.ts";

export function createClient() {
  const { supabaseUrl, supabasePublishableKey } = getPublicSupabaseEnvironment();
  return createBrowserClient(supabaseUrl, supabasePublishableKey);
}
