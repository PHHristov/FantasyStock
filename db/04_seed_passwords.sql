-- Seeds dev passwords for the built-in user1/user2 accounts so they can log
-- in via POST /auth/login. Hashes are bcrypt ($2a$, via pgcrypto), which
-- BCrypt.Net in the backend verifies natively. Only fills in a password
-- where none is set yet, so it never overwrites a password changed later.
-- Dev-only credentials - see README.md "Seeded users".

CREATE EXTENSION IF NOT EXISTS pgcrypto;

UPDATE users SET password_hash = crypt('user1pass', gen_salt('bf'))
WHERE name = 'user1' AND password_hash IS NULL;

UPDATE users SET password_hash = crypt('user2pass', gen_salt('bf'))
WHERE name = 'user2' AND password_hash IS NULL;
