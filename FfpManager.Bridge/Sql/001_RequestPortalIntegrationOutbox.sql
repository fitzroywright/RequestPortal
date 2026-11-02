IF DB_NAME() <> N'FFP' OR UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01'
    THROW 51050, 'STOP: Only JMDB01/FFP is authorized.', 1;
GO

/*
Request Portal integration outbox for FFP Manager.

FFP Manager must enqueue an event in the SAME SQL transaction that changes the
ApplicationLetter. The bridge drains this table after the business transaction commits.

Do not add a trigger. The application explicitly calls the procedures below.
*/

IF OBJECT_ID(N'dbo.RequestPortalIntegrationOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RequestPortalIntegrationOutbox
    (
        SequenceNo bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_RequestPortalIntegrationOutbox PRIMARY KEY,
        EventId uniqueidentifier NOT NULL
            CONSTRAINT DF_RequestPortalIntegrationOutbox_EventId DEFAULT NEWSEQUENTIALID(),
        EventType nvarchar(100) NOT NULL,
        ApplicationLetterId bigint NOT NULL,
        PortalRequestId uniqueidentifier NOT NULL,
        StatusCode nvarchar(255) NULL,
        StatusText nvarchar(255) NULL,
        Detail nvarchar(max) NULL,
        Message nvarchar(max) NULL,
        RequestedBy nvarchar(255) NULL,
        DueUtc datetime2(3) NULL,
        OccurredUtc datetime2(3) NOT NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_RequestPortalIntegrationOutbox_AttemptCount DEFAULT 0,
        NextAttemptUtc datetime2(3) NULL,
        LockedUntilUtc datetime2(3) NULL,
        DeliveredUtc datetime2(3) NULL,
        LastError nvarchar(2000) NULL,
        CreatedBy nvarchar(100) NULL,
        DateCreated datetime2(3) NOT NULL
            CONSTRAINT DF_RequestPortalIntegrationOutbox_DateCreated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_RequestPortalIntegrationOutbox_EventId UNIQUE (EventId)
    );

    CREATE INDEX IX_RequestPortalIntegrationOutbox_Pending
        ON dbo.RequestPortalIntegrationOutbox
        (
            DeliveredUtc,
            NextAttemptUtc,
            LockedUntilUtc,
            SequenceNo
        )
        INCLUDE
        (
            EventId,
            EventType,
            ApplicationLetterId,
            PortalRequestId,
            AttemptCount
        );
END;
GO

CREATE OR ALTER PROCEDURE dbo.RequestPortal_EnqueueApplicationStatusChanged
    @ApplicationLetterId bigint,
    @EventId uniqueidentifier = NULL,
    @OccurredUtc datetime2(3) = NULL,
    @CreatedBy nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ResolvedEventId uniqueidentifier = COALESCE(@EventId, NEWID());
    DECLARE @ResolvedOccurredUtc datetime2(3) = COALESCE(@OccurredUtc, SYSUTCDATETIME());

    IF EXISTS
    (
        SELECT 1
        FROM dbo.RequestPortalIntegrationOutbox
        WHERE EventId = @ResolvedEventId
    )
    BEGIN
        RETURN;
    END;

    INSERT INTO dbo.RequestPortalIntegrationOutbox
    (
        EventId,
        EventType,
        ApplicationLetterId,
        PortalRequestId,
        StatusCode,
        StatusText,
        Detail,
        OccurredUtc,
        CreatedBy
    )
    SELECT
        @ResolvedEventId,
        N'ApplicationStatusChanged',
        al.ID,
        al.ExternalRequestId,
        COALESCE(ast.Code, ast.Name, N'RECEIVED'),
        COALESCE(ast.Name, ast.Code, N'Received'),
        ast.Description,
        @ResolvedOccurredUtc,
        @CreatedBy
    FROM dbo.ApplicationLetters al
    LEFT JOIN dbo.ApplicationStatusTypes ast
        ON ast.ID = al.ApplicationStatus
    WHERE
        al.ID = @ApplicationLetterId
        AND al.GCRecord IS NULL
        AND al.ExternalRequestId IS NOT NULL;

    IF @@ROWCOUNT <> 1
    BEGIN
        THROW 51001, 'ApplicationLetter is missing, deleted, or is not linked to Request Portal.', 1;
    END;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RequestPortal_EnqueueInformationRequest
    @ApplicationLetterId bigint,
    @Message nvarchar(max),
    @RequestedBy nvarchar(255) = NULL,
    @DueUtc datetime2(3) = NULL,
    @EventId uniqueidentifier = NULL,
    @OccurredUtc datetime2(3) = NULL,
    @CreatedBy nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NULLIF(LTRIM(RTRIM(@Message)), N'') IS NULL
    BEGIN
        THROW 51002, 'Information request message is required.', 1;
    END;

    DECLARE @ResolvedEventId uniqueidentifier = COALESCE(@EventId, NEWID());
    DECLARE @ResolvedOccurredUtc datetime2(3) = COALESCE(@OccurredUtc, SYSUTCDATETIME());

    IF EXISTS
    (
        SELECT 1
        FROM dbo.RequestPortalIntegrationOutbox
        WHERE EventId = @ResolvedEventId
    )
    BEGIN
        RETURN;
    END;

    DECLARE @MessageCode nvarchar(25) = LEFT(REPLACE(CONVERT(nvarchar(36), @ResolvedEventId), N'-', N''), 25);

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.ApplicationNotes
        WHERE ApplicationLetterID = @ApplicationLetterId
          AND Code = @MessageCode
          AND NoteType = N'Request Portal Information Request'
    )
    BEGIN
        INSERT INTO dbo.ApplicationNotes
        (
            DateCreated, Code, Name, Description, UserName, DateUpdated,
            ApplicationLetterID, NoteType, CreatedBy
        )
        VALUES
        (
            @ResolvedOccurredUtc, @MessageCode, N'Request information',
            @Message, @CreatedBy, @ResolvedOccurredUtc,
            @ApplicationLetterId, N'Request Portal Information Request', @CreatedBy
        );
    END;

    INSERT INTO dbo.RequestPortalIntegrationOutbox
    (
        EventId,
        EventType,
        ApplicationLetterId,
        PortalRequestId,
        Message,
        RequestedBy,
        DueUtc,
        OccurredUtc,
        CreatedBy
    )
    SELECT
        @ResolvedEventId,
        N'InformationRequested',
        al.ID,
        al.ExternalRequestId,
        @Message,
        @RequestedBy,
        @DueUtc,
        @ResolvedOccurredUtc,
        @CreatedBy
    FROM dbo.ApplicationLetters al
    WHERE
        al.ID = @ApplicationLetterId
        AND al.GCRecord IS NULL
        AND al.ExternalRequestId IS NOT NULL;

    IF @@ROWCOUNT <> 1
    BEGIN
        THROW 51003, 'ApplicationLetter is missing, deleted, or is not linked to Request Portal.', 1;
    END;
END;
GO
