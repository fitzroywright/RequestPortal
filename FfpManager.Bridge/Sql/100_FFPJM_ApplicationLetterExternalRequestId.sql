-- Production only. See docs/FFPJM-PRODUCTION-CUTOVER.md before execution.
IF DB_NAME() <> N'FFPJM' OR UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01'
    THROW 51050, 'STOP: Only JMDB01/FFPJM is authorized.', 1;
IF COALESCE(CONVERT(nvarchar(4000), SESSION_CONTEXT(N'FFPJM.VerifiedBackupEvidence')), N'') = N''
    THROW 51053, 'STOP: Current restorable FFPJM backup evidence must be recorded before migration.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ApplicationLetters') AND name=N'ExternalRequestId' AND (TYPE_NAME(user_type_id)<>N'uniqueidentifier' OR is_nullable<>1))
    THROW 51054, 'STOP: Existing ExternalRequestId is incompatible.', 1;
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
IF DB_NAME() <> N'FFPJM' OR UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01'
    THROW 51050, 'STOP: Only JMDB01/FFPJM is authorized.', 1;
IF COALESCE(CONVERT(nvarchar(4000), SESSION_CONTEXT(N'FFPJM.VerifiedBackupEvidence')), N'') = N''
    THROW 51053, 'STOP: Current restorable FFPJM backup evidence must be recorded before migration.', 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ApplicationLetters') AND name=N'UX_ApplicationLetters_ExternalRequestId')
AND NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.object_id=OBJECT_ID(N'dbo.ApplicationLetters') AND i.name=N'UX_ApplicationLetters_ExternalRequestId'
      AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=1
      AND REPLACE(REPLACE(REPLACE(i.filter_definition,N'[',N''),N']',N''),N' ',N'')=N'(ExternalRequestIdISNOTNULL)'
      AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                  WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'ExternalRequestId')
)
    THROW 51054, 'STOP: Existing correlation index is incompatible; review without replacement.', 1;

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
