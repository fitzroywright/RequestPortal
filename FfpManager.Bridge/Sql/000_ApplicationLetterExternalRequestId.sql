/*
Adds the dedicated Request Portal correlation key to FFP Manager ApplicationLetters.

This is intentionally separate from MessageID. The integration must not reuse or
reinterpret MessageID.

Safe to apply before the Request Portal integration is enabled:
- nullable column
- no backfill
- no existing ApplicationLetter values are changed
*/

IF COL_LENGTH(N'dbo.ApplicationLetters', N'ExternalRequestId') IS NULL
BEGIN
    ALTER TABLE dbo.ApplicationLetters
        ADD ExternalRequestId uniqueidentifier NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE
        object_id = OBJECT_ID(N'dbo.ApplicationLetters')
        AND name = N'UX_ApplicationLetters_ExternalRequestId'
)
BEGIN
    CREATE UNIQUE INDEX UX_ApplicationLetters_ExternalRequestId
        ON dbo.ApplicationLetters (ExternalRequestId)
        WHERE ExternalRequestId IS NOT NULL;
END;
GO
