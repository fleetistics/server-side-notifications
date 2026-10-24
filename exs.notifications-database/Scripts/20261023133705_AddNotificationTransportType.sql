START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory_Notifications" WHERE "MigrationId" = '20261023133705_AddNotificationTransportType') THEN
    ALTER TABLE notification ADD "TransportType" smallint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory_Notifications" WHERE "MigrationId" = '20261023133705_AddNotificationTransportType') THEN
    ALTER TABLE notification_outbox ALTER COLUMN "UserId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory_Notifications" WHERE "MigrationId" = '20261023133705_AddNotificationTransportType') THEN
    ALTER TABLE notification_outbox ALTER COLUMN "NotificationId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory_Notifications" WHERE "MigrationId" = '20261023133705_AddNotificationTransportType') THEN
    INSERT INTO "__EFMigrationsHistory_Notifications" ("MigrationId", "ProductVersion")
    VALUES ('20261023133705_AddNotificationTransportType', '10.0.11');
    END IF;
END $EF$;
COMMIT;

