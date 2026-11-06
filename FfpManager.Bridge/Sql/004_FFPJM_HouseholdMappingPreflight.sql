/* READ ONLY. Run as the dedicated Kerberos bridge identity on JMDB01/FFPJM.
   No production schema or business data changes. Retain every result set.
   Backup history alone does not prove a verified, restorable backup exists. */
SET NOCOUNT ON;
IF UPPER(CONVERT(nvarchar(128),DB_NAME())) <> N'FFPJM'
    THROW 51050, 'STOP: Production requires FFPJM.', 1;
IF UPPER(CONVERT(nvarchar(128),SERVERPROPERTY('MachineName'))) <> N'JMDB01'
    THROW 51051, 'STOP: Production requires JMDB01.', 1;
IF LOWER(ORIGINAL_LOGIN()) <> N'fftpj\svc_ffpbridge'
   OR CONVERT(nvarchar(128),CONNECTIONPROPERTY('auth_scheme')) <> N'KERBEROS'
    THROW 51052, 'STOP: Dedicated bridge Kerberos identity required.', 1;
SELECT DB_NAME() AS DatabaseName, SERVERPROPERTY('MachineName') AS MachineName,
       ORIGINAL_LOGIN() AS ServiceIdentity, CONNECTIONPROPERTY('auth_scheme') AS Authentication,
       SYSDATETIMEOFFSET() AS ObservedAt;

SELECT c.name, TYPE_NAME(c.system_type_id) AS SystemType,
       TYPE_NAME(c.user_type_id) AS DeclaredType, c.max_length AS MaximumBytes,
       c.precision, c.scale, c.is_nullable, c.is_computed,
       dc.definition AS DefaultDefinition
FROM sys.columns c LEFT JOIN sys.default_constraints dc
 ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
WHERE c.object_id=OBJECT_ID(N'dbo.ApplicationLetters')
 AND c.name IN(N'ID',N'ExternalRequestId',N'Occupation',N'Dependents',N'DateUpdated',N'OptimisticLockField')
ORDER BY c.column_id;

-- Include table-level checks and triggers: these may govern multiple fields.
SELECT name, definition, is_disabled, is_not_trusted
FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.ApplicationLetters');
SELECT fk.name, pc.name AS SourceColumn, SCHEMA_NAME(rt.schema_id) AS TargetSchema,
       rt.name AS TargetTable, rc.name AS TargetColumn, fk.is_disabled, fk.is_not_trusted
FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
JOIN sys.tables rt ON rt.object_id=fkc.referenced_object_id
JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fkc.referenced_column_id
WHERE fk.parent_object_id=OBJECT_ID(N'dbo.ApplicationLetters');
SELECT tr.name, tr.is_disabled, sm.definition
FROM sys.triggers tr LEFT JOIN sys.sql_modules sm ON sm.object_id=tr.object_id
WHERE tr.parent_id=OBJECT_ID(N'dbo.ApplicationLetters');
SELECT i.name, i.is_unique, i.has_filter, i.filter_definition, c.name AS ColumnName
FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=OBJECT_ID(N'dbo.ApplicationLetters') AND c.name=N'ExternalRequestId';

-- Optional catalog evidence; a DBA may need to supply this result separately.
-- Confirm retained VERIFYONLY / isolated restore evidence before authorizing writes.
SELECT TOP (5) bs.backup_set_uuid, bs.backup_start_date, bs.backup_finish_date,
       bs.type, bs.position, bs.has_backup_checksums, bs.is_damaged,
       bs.first_lsn, bs.last_lsn, bs.database_backup_lsn
FROM msdb.dbo.backupset bs
WHERE bs.database_name=N'FFPJM' AND bs.type=N'D'
ORDER BY bs.backup_finish_date DESC;
