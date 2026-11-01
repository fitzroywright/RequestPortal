/*
FFP Manager Bridge: preflight for the FFP rehearsal database.
READ ONLY. Run in SSMS on JMDB01 with the FFP database selected.
This is deliberately NOT a schema migration.
Never run against FFPJM or any other database.
*/
SET NOCOUNT ON;

IF UPPER(CONVERT(nvarchar(128), DB_NAME())) <> N'FFP'
    THROW 51050, 'STOP: Rehearsal requires database FFP, not FFPJM or another database.', 1;

IF UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'))) NOT IN (N'JMDB01', N'JMDB01\\MSSQLSERVER')
   AND UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01'
    THROW 51051, 'STOP: Rehearsal requires SQL Server JMDB01.', 1;

SELECT
    @@SERVERNAME AS ServerName,
    DB_NAME() AS DatabaseName,
    SUSER_SNAME() AS LoginName,
    SYSDATETIMEOFFSET() AS CheckedAt;

DECLARE @RequiredObjects table (ObjectName sysname NOT NULL, ObjectType char(2) NOT NULL);
INSERT @RequiredObjects (ObjectName, ObjectType)
VALUES
    (N'ApplicationLetters', 'U'),
    (N'ItemsRequested', 'U'),
    (N'SupportingDocument', 'U'),
    (N'ApplicationNotes', 'U'),
    (N'ApplicationStatusTypes', 'U'),
    (N'DocumentTypes', 'U'),
    (N'UnitsOfMeasure', 'U'),
    (N'Parishes', 'U'),
    (N'IDGeneratorTable', 'U'),
    (N'RequestPortalIntegrationOutbox', 'U'),
    (N'RequestPortal_EnqueueApplicationStatusChanged', 'P'),
    (N'RequestPortal_EnqueueInformationRequest', 'P');

SELECT
    r.ObjectName,
    r.ObjectType,
    CASE WHEN OBJECT_ID(N'dbo.' + r.ObjectName, r.ObjectType) IS NULL
         THEN N'MISSING' ELSE N'PRESENT' END AS Result
FROM @RequiredObjects AS r
ORDER BY r.ObjectType, r.ObjectName;

SELECT
    N'ApplicationLetters.ExternalRequestId' AS CheckName,
    CASE WHEN COL_LENGTH(N'dbo.ApplicationLetters', N'ExternalRequestId') IS NULL
         THEN N'MISSING' ELSE N'PRESENT' END AS Result
UNION ALL
SELECT
    N'UX_ApplicationLetters_ExternalRequestId',
    CASE WHEN EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ApplicationLetters')
          AND name = N'UX_ApplicationLetters_ExternalRequestId'
          AND is_unique = 1
          AND has_filter = 1
    ) THEN N'PRESENT' ELSE N'MISSING/INCOMPATIBLE' END;

SELECT
    t.name AS TableName,
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length AS MaxLengthBytes,
    c.is_nullable AS IsNullable,
    c.is_identity AS IsIdentity
FROM sys.tables AS t
INNER JOIN sys.columns AS c ON c.object_id = t.object_id
WHERE SCHEMA_NAME(t.schema_id) = N'dbo'
  AND t.name IN (N'ApplicationLetters', N'SupportingDocument', N'IDGeneratorTable')
  AND
  (
      (t.name = N'ApplicationLetters' AND
       c.name IN (N'ID', N'Code', N'ReferenceNo', N'ExternalRequestId',
                  N'Parish', N'DocumentSource', N'ApplicationStatus'))
   OR (t.name = N'SupportingDocument' AND
       c.name IN (N'Oid', N'ApplicationLetter', N'FileName', N'DocumentType'))
   OR t.name = N'IDGeneratorTable'
  )
ORDER BY t.name, c.column_id;

-- Reference data used by the bridge; counts only, no applicant records.
IF OBJECT_ID(N'dbo.ApplicationStatusTypes', 'U') IS NOT NULL
    SELECT N'ApplicationStatusTypes' AS LookupTable, COUNT_BIG(*) AS RowCount
    FROM dbo.ApplicationStatusTypes;
IF OBJECT_ID(N'dbo.DocumentTypes', 'U') IS NOT NULL
    SELECT N'DocumentTypes' AS LookupTable, COUNT_BIG(*) AS RowCount
    FROM dbo.DocumentTypes;
IF OBJECT_ID(N'dbo.UnitsOfMeasure', 'U') IS NOT NULL
    SELECT N'UnitsOfMeasure' AS LookupTable, COUNT_BIG(*) AS RowCount
    FROM dbo.UnitsOfMeasure;

-- Follow up with the read-only counter compatibility test in
-- FfpManagerReferenceNumberTests before allowing a production-style submission.
-- Never initialize or reset the FFP Manager IDGeneratorTable counter manually.
