export type PublicSupabaseEnvironment = {
  apiBaseUrl: string;
  supabaseUrl: string;
  supabasePublishableKey: string;
};

type PublicEnvironment = Record<string, string | undefined>;

export function getPublicSupabaseEnvironment(
  environment: PublicEnvironment = {
    NEXT_PUBLIC_API_BASE_URL: process.env.NEXT_PUBLIC_API_BASE_URL,
    NEXT_PUBLIC_SUPABASE_URL: process.env.NEXT_PUBLIC_SUPABASE_URL,
    NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY:
      process.env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY,
  },
): PublicSupabaseEnvironment {
  const apiBaseUrl = requireValue(
    "NEXT_PUBLIC_API_BASE_URL",
    environment.NEXT_PUBLIC_API_BASE_URL,
  );
  const supabaseUrl = requireValue(
    "NEXT_PUBLIC_SUPABASE_URL",
    environment.NEXT_PUBLIC_SUPABASE_URL,
  );
  const supabasePublishableKey = requireValue(
    "NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY",
    environment.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY,
  );

  validateHttpUrl("NEXT_PUBLIC_API_BASE_URL", apiBaseUrl);
  validateSupabaseUrl(supabaseUrl);
  if (isSecretKey(supabasePublishableKey)) {
    throw new Error("NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY must not contain a secret or service-role key.");
  }

  return { apiBaseUrl, supabaseUrl, supabasePublishableKey };
}

function requireValue(settingName: string, value: string | undefined): string {
  if (!value || !value.trim()) {
    throw new Error(`${settingName} is required.`);
  }
  if (/project_ref|your[-_]?key|change[-_]?me|replace[_ -]?me|example/i.test(value)) {
    throw new Error(`${settingName} must not be a placeholder.`);
  }
  return value;
}

function validateHttpUrl(settingName: string, value: string): void {
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") {
      throw new Error();
    }
  } catch {
    throw new Error(`${settingName} must be an HTTP or HTTPS URL.`);
  }
}

function validateSupabaseUrl(value: string): void {
  try {
    const url = new URL(value);
    if (url.protocol !== "https:" || !url.hostname.endsWith(".supabase.co")) {
      throw new Error();
    }
  } catch {
    throw new Error("NEXT_PUBLIC_SUPABASE_URL must be an HTTPS Supabase hosted URL.");
  }
}

function isSecretKey(value: string): boolean {
  return /sb_secret_|service[-_]?role|(?:^|[-_])secret(?:[-_]|$)/i.test(value);
}
