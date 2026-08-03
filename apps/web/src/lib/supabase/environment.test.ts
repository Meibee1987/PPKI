import assert from "node:assert/strict";
import test from "node:test";
import { getPublicSupabaseEnvironment } from "./environment.ts";

const validEnvironment = {
  NEXT_PUBLIC_API_BASE_URL: "http://localhost:8080",
  NEXT_PUBLIC_SUPABASE_URL: "https://valid-project.supabase.co",
  NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY: "sb_publishable_valid",
};

test("rejects missing, empty, and placeholder public configuration without exposing values", () => {
  for (const environment of [
    { ...validEnvironment, NEXT_PUBLIC_SUPABASE_URL: undefined },
    { ...validEnvironment, NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY: "   " },
    { ...validEnvironment, NEXT_PUBLIC_API_BASE_URL: "https://PROJECT_REF.example" },
  ]) {
    assert.throws(
      () => getPublicSupabaseEnvironment(environment),
      (error: Error) => !error.message.includes("not-for-error"),
    );
  }
});

test("rejects non-HTTPS Supabase URLs and secret public keys", () => {
  assert.throws(
    () => getPublicSupabaseEnvironment({ ...validEnvironment, NEXT_PUBLIC_SUPABASE_URL: "http://valid-project.supabase.co" }),
    /NEXT_PUBLIC_SUPABASE_URL/,
  );
  assert.throws(
    () => getPublicSupabaseEnvironment({ ...validEnvironment, NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY: "sb_secret_not-for-error" }),
    (error: Error) =>
      error.message.includes("NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY")
      && !error.message.includes("not-for-error"),
  );
});

test("accepts valid public configuration without making a network connection", () => {
  assert.deepEqual(getPublicSupabaseEnvironment(validEnvironment), {
    apiBaseUrl: "http://localhost:8080",
    supabaseUrl: "https://valid-project.supabase.co",
    supabasePublishableKey: "sb_publishable_valid",
  });
});

test("reads the default public configuration through statically analyzable environment access", () => {
  const previous = {
    NEXT_PUBLIC_API_BASE_URL: process.env.NEXT_PUBLIC_API_BASE_URL,
    NEXT_PUBLIC_SUPABASE_URL: process.env.NEXT_PUBLIC_SUPABASE_URL,
    NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY:
      process.env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY,
  };

  try {
    Object.assign(process.env, validEnvironment);
    assert.deepEqual(getPublicSupabaseEnvironment(), {
      apiBaseUrl: "http://localhost:8080",
      supabaseUrl: "https://valid-project.supabase.co",
      supabasePublishableKey: "sb_publishable_valid",
    });
  } finally {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});
