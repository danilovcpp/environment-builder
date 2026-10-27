CREATE TABLE InitializationAttempts (idempotencyKey TEXT NOT NULL, attempt INTEGER NOT NULL, state TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(idempotencyKey,attempt,state));
UPDATE AppMetadata SET value='2' WHERE key='databaseSchemaVersion';
PRAGMA user_version=2;
