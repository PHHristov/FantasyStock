-- Adds password auth on top of 02_app_schema.sql's users table. Nullable so
-- it applies cleanly to the already-seeded user1/user2 rows; their dev
-- passwords are set by 04_seed_passwords.sql. The db-migrate compose service
-- re-applies this on every start, so existing volumes pick it up too.

ALTER TABLE users ADD COLUMN IF NOT EXISTS password_hash VARCHAR(255);

-- 02_app_schema.sql seeded user1/user2 with explicit `id` values (1, 2),
-- which does NOT advance the `id` SERIAL sequence - it stays at its
-- default start, so the *first* real INSERT (POST /auth/register, the
-- first feature to insert a user without specifying `id`) would collide
-- with user1's id=1 and fail with a misleading "unique violation" that
-- looks like a username conflict. Fast-forward the sequence past the
-- highest already-seeded id so new inserts pick up from there.
SELECT setval('users_id_seq', (SELECT MAX(id) FROM users));
