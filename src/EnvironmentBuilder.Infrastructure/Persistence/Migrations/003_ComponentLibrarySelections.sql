CREATE TABLE ComponentLibrarySelections (
    definitionId TEXT PRIMARY KEY,
    version TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    FOREIGN KEY(definitionId,version) REFERENCES ComponentDefinitions(definitionId,version)
);
UPDATE AppMetadata SET value='3' WHERE key='databaseSchemaVersion';
PRAGMA user_version=3;
