-- V1.6: Tenants.ApiKeyHash — SHA-256 of the plaintext ApiKey used for tenant
-- lookup. Lookup now prefers the hash (digest is harmless to expose on reads);
-- plaintext ApiKey remains as a compatibility fallback until keys rotate.

BEGIN;

ALTER TABLE "Tenants"
    ADD COLUMN IF NOT EXISTS "ApiKeyHash" varchar(128) NULL;

CREATE INDEX IF NOT EXISTS "IX_Tenants_ApiKeyHash"
    ON "Tenants"("ApiKeyHash");

-- Backfill when pgcrypto is available (digest()). If the extension is absent
-- this block no-ops; lookups fall back to plaintext matching in the meantime.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgcrypto') THEN
        UPDATE "Tenants"
        SET "ApiKeyHash" = encode(digest("ApiKey", 'sha256'), 'hex')
        WHERE "ApiKeyHash" IS NULL;
    END IF;
END $$;

COMMIT;
