-- READ ONLY. Use the established Request Portal PostgreSQL service identity.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='30s';
DO $guard$ BEGIN
 IF current_database()<>'requestportal' THEN RAISE EXCEPTION 'Expected production Request Portal database'; END IF;
END $guard$;
SELECT current_database(),current_user,current_schema(),inet_server_addr(),version();
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT table_schema,table_name,column_name,data_type,is_nullable,character_maximum_length
FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name IN ('outbox_messages','management_dashboard_aggregate')
ORDER BY table_name,ordinal_position;
SELECT tablename,tableowner FROM pg_tables WHERE schemaname=current_schema()
 AND tablename IN ('assistance_requests','request_status_history','quick_intakes','documents','deliveries',
 'delivery_returns','organizations','request_categories','program_classifications','integration_links','outbox_messages');
SELECT has_schema_privilege(current_schema(),'CREATE') AS CanCreateFunctionAndTable,
       has_table_privilege('outbox_messages','INSERT') AS CanEnqueueRefresh;
SELECT event_object_table,trigger_name,action_timing,event_manipulation
FROM information_schema.triggers WHERE trigger_schema=current_schema()
ORDER BY event_object_table,trigger_name;
ROLLBACK;
-- A DBA must separately provide current backup identity/time and restore verification evidence.
-- After deployment, execute these SELECTs under a separate READ ONLY transaction:
-- SELECT "Id","SnapshotVersion","LastUpdatedUtc","LastReconciledUtc","RefreshStatus","AttemptCount",
--        jsonb_typeof("AggregateData") AS JsonType FROM management_dashboard_aggregate;
-- SELECT COUNT(*) FROM outbox_messages WHERE "MessageType"='RequestPortal.ManagementDashboardRefreshRequested.v1' AND "ProcessedUtc" IS NULL;
