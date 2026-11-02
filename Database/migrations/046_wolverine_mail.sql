-- WolverineFx.SqlServer 6.59.1 / commit e9110b753f3383054442ab5e656e26395b80e667.
-- Generated message-store and SQL-transport DDL; native gameplay/mail tables are unchanged.
-- Production uses AutoCreate.None. This migration provisions both Map and World mail queues.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;


IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'fourdev_mail' )
    EXEC('CREATE SCHEMA [fourdev_mail]');


IF OBJECT_ID('fourdev_mail.wolverine_outgoing_envelopes') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_outgoing_envelopes (
    id              uniqueidentifier    NOT NULL,
    owner_id        int                 NOT NULL,
    destination     varchar(250)        NOT NULL,
    deliver_by      datetimeoffset      NULL,
    body            varbinary(max)      NOT NULL,
    attempts        int                 NULL DEFAULT 0,
    message_type    varchar(250)        NOT NULL,
CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_outgoing_envelopes_owner' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_outgoing_envelopes'))
    CREATE INDEX idx_wolverine_outgoing_envelopes_owner ON fourdev_mail.wolverine_outgoing_envelopes (owner_id) WHERE ([owner_id]<>0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_outgoing_envelopes_recover' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_outgoing_envelopes'))
    CREATE INDEX idx_wolverine_outgoing_envelopes_recover ON fourdev_mail.wolverine_outgoing_envelopes (destination) WHERE ([owner_id]=0);
IF OBJECT_ID('fourdev_mail.wolverine_incoming_envelopes') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_incoming_envelopes (
    id                uniqueidentifier    NOT NULL,
    status            varchar(25)         NOT NULL,
    owner_id          int                 NOT NULL,
    execution_time    datetimeoffset      NULL DEFAULT NULL,
    attempts          int                 NULL DEFAULT 0,
    body              varbinary(max)      NOT NULL,
    message_type      varchar(250)        NOT NULL,
    received_at       varchar(250)        NOT NULL,
    keep_until        datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_owner' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_owner ON fourdev_mail.wolverine_incoming_envelopes (owner_id) WHERE ([owner_id]<>0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_recover' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_recover ON fourdev_mail.wolverine_incoming_envelopes (received_at) WHERE ([status]='Incoming' AND [owner_id]=0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_keep_until' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_keep_until ON fourdev_mail.wolverine_incoming_envelopes (keep_until) WHERE ([status]='Handled');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_scheduled' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_scheduled ON fourdev_mail.wolverine_incoming_envelopes (execution_time) WHERE ([status]='Scheduled');
IF OBJECT_ID('fourdev_mail.wolverine_dead_letters') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_dead_letters (
    id                   uniqueidentifier    NOT NULL,
    execution_time       datetimeoffset      NULL DEFAULT NULL,
    body                 varbinary(max)      NOT NULL,
    message_type         varchar(250)        NOT NULL,
    received_at          varchar(250)        NOT NULL,
    source               varchar(250)        NULL,
    exception_type       varchar(max)        NULL,
    exception_message    varchar(max)        NULL,
    sent_at              datetimeoffset      NULL,
    replayable           bit                 NULL,
    failure_group        varchar(100)        NULL,
    failure_title        nvarchar(500)       NULL,
    failed_at            datetimeoffset      NULL,
    first_failed_at      datetimeoffset      NULL,
    replay_count         int                 NULL,
    last_replayed_at     datetimeoffset      NULL,
    archived_at          datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letters_replayable' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_dead_letters'))
    CREATE INDEX idx_wolverine_dead_letters_replayable ON fourdev_mail.wolverine_dead_letters (replayable) WHERE ([replayable]=1);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letters_failure_group' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_dead_letters'))
    CREATE INDEX idx_wolverine_dead_letters_failure_group ON fourdev_mail.wolverine_dead_letters (failure_group);
IF OBJECT_ID('fourdev_mail.wolverine_dead_letter_history') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_dead_letter_history (
    id                  uniqueidentifier    NOT NULL,
    replay_count        int                 NULL,
    last_replayed_at    datetimeoffset      NULL,
    first_failed_at     datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_dead_letter_history_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letter_history_last_replayed_at' AND object_id = OBJECT_ID(N'fourdev_mail.wolverine_dead_letter_history'))
    CREATE INDEX idx_wolverine_dead_letter_history_last_replayed_at ON fourdev_mail.wolverine_dead_letter_history (last_replayed_at);
IF TYPE_ID(N'fourdev_mail.EnvelopeIdList') IS NULL
CREATE TYPE fourdev_mail.EnvelopeIdList AS TABLE (id UNIQUEIDENTIFIER NOT NULL)
GO
CREATE OR ALTER PROCEDURE fourdev_mail.uspDeleteIncomingEnvelopes
    @IDLIST fourdev_mail.EnvelopeIdList READONLY
AS

DELETE
FROM fourdev_mail.wolverine_incoming_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE fourdev_mail.uspDeleteOutgoingEnvelopes
    @IDLIST fourdev_mail.EnvelopeIdList READONLY
AS

DELETE
FROM fourdev_mail.wolverine_outgoing_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE fourdev_mail.uspDiscardAndReassignOutgoing
    @DISCARDS fourdev_mail.EnvelopeIdList READONLY,
    @REASSIGNED fourdev_mail.EnvelopeIdList READONLY,
    @OWNERID INT

AS

DELETE
FROM fourdev_mail.wolverine_outgoing_envelopes
WHERE id IN (SELECT ID FROM @DISCARDS);

UPDATE fourdev_mail.wolverine_outgoing_envelopes
SET owner_id = @OWNERID
WHERE ID IN (SELECT ID FROM @REASSIGNED);

GO
GO
CREATE OR ALTER PROCEDURE fourdev_mail.uspMarkIncomingOwnership
    @IDLIST fourdev_mail.EnvelopeIdList READONLY,
    @owner INT
AS

-- GH-4216: without the status predicate this also rewrites rows that are already Incoming or Handled.
-- The load side of the poll selects only Scheduled rows, so constraining the update to match costs nothing
-- and keeps the promotion from touching a row it never selected.
UPDATE fourdev_mail.wolverine_incoming_envelopes
SET owner_id = @owner, status = 'Incoming'
WHERE status = 'Scheduled' AND id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE fourdev_mail.uspMarkOutgoingOwnership
    @IDLIST fourdev_mail.EnvelopeIdList READONLY,
    @owner INT
AS

UPDATE fourdev_mail.wolverine_outgoing_envelopes
SET owner_id = @owner
WHERE id IN (SELECT ID FROM @IDLIST);

GO
IF OBJECT_ID('fourdev_mail.wolverine_nodes') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_nodes (
    id              uniqueidentifier    NOT NULL,
    node_number     int                 NOT NULL IDENTITY,
    description     varchar(max)        NOT NULL,
    uri             varchar(500)        NOT NULL,
    started         datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    health_check    datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    version         varchar(500)        NULL,
    capabilities    nvarchar(max)       NULL,
CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('fourdev_mail.wolverine_node_assignments') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_node_assignments (
    id         varchar(500)        NOT NULL,
    node_id    uniqueidentifier    NULL,
    started    datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id)
);
END

IF OBJECT_ID(N'fourdev_mail.fkey_wolverine_node_assignments_node_id', N'F') IS NULL
ALTER TABLE fourdev_mail.wolverine_node_assignments
ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY(node_id)
 REFERENCES fourdev_mail.wolverine_nodes(id)ON DELETE CASCADE
;

IF OBJECT_ID('fourdev_mail.wolverine_control_queue') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_control_queue (
    id              uniqueidentifier    NOT NULL,
    message_type    varchar(500)        NOT NULL,
    node_id         uniqueidentifier    NOT NULL,
    body            varbinary(max)      NOT NULL,
    posted          datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    expires         datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('fourdev_mail.wolverine_agent_restrictions') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_agent_restrictions (
    id      uniqueidentifier    NOT NULL,
    uri     varchar(500)        NOT NULL,
    type    varchar(500)        NOT NULL,
    node    int                 NOT NULL DEFAULT 0,
CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('fourdev_mail.wolverine_node_records') IS NULL
BEGIN
CREATE TABLE fourdev_mail.wolverine_node_records (
    id             int               NOT NULL IDENTITY,
    node_number    int               NOT NULL,
    event_name     varchar(500)      NOT NULL,
    timestamp      datetimeoffset    NOT NULL DEFAULT GETUTCDATE(),
    description    varchar(1000)     NULL,
CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id)
);
END

GO
IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'fourdev_mail_transport' )
    EXEC('CREATE SCHEMA [fourdev_mail_transport]');
IF OBJECT_ID('fourdev_mail_transport.wolverine_queue_mail_delivery') IS NULL
BEGIN
CREATE TABLE fourdev_mail_transport.wolverine_queue_mail_delivery (
    id              uniqueidentifier    NOT NULL,
    body            varbinary(max)      NOT NULL,
    message_type    varchar(250)        NOT NULL,
    keep_until      datetimeoffset      NULL,
    timestamp       datetimeoffset      NULL DEFAULT SYSDATETIMEOFFSET(),
CONSTRAINT pkey_wolverine_queue_mail_delivery_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_queue_mail_delivery_timestamp' AND object_id = OBJECT_ID(N'fourdev_mail_transport.wolverine_queue_mail_delivery'))
    CREATE INDEX idx_wolverine_queue_mail_delivery_timestamp ON fourdev_mail_transport.wolverine_queue_mail_delivery (timestamp);
IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'fourdev_mail_transport' )
    EXEC('CREATE SCHEMA [fourdev_mail_transport]');
IF OBJECT_ID('fourdev_mail_transport.wolverine_queue_mail_delivery_scheduled') IS NULL
BEGIN
CREATE TABLE fourdev_mail_transport.wolverine_queue_mail_delivery_scheduled (
    id                uniqueidentifier    NOT NULL,
    body              varbinary(max)      NOT NULL,
    message_type      varchar(250)        NOT NULL,
    execution_time    datetimeoffset      NOT NULL,
    keep_until        datetimeoffset      NULL,
    timestamp         datetimeoffset      NULL DEFAULT SYSDATETIMEOFFSET(),
CONSTRAINT pkey_wolverine_queue_mail_delivery_scheduled_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_queue_mail_delivery_scheduled_execution_time' AND object_id = OBJECT_ID(N'fourdev_mail_transport.wolverine_queue_mail_delivery_scheduled'))
    CREATE INDEX idx_wolverine_queue_mail_delivery_scheduled_execution_time ON fourdev_mail_transport.wolverine_queue_mail_delivery_scheduled (execution_time);
IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'fourdev_mail_transport' )
    EXEC('CREATE SCHEMA [fourdev_mail_transport]');
IF OBJECT_ID('fourdev_mail_transport.wolverine_queue_mail_notifications') IS NULL
BEGIN
CREATE TABLE fourdev_mail_transport.wolverine_queue_mail_notifications (
    id              uniqueidentifier    NOT NULL,
    body            varbinary(max)      NOT NULL,
    message_type    varchar(250)        NOT NULL,
    keep_until      datetimeoffset      NULL,
    timestamp       datetimeoffset      NULL DEFAULT SYSDATETIMEOFFSET(),
CONSTRAINT pkey_wolverine_queue_mail_notifications_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_queue_mail_notifications_timestamp' AND object_id = OBJECT_ID(N'fourdev_mail_transport.wolverine_queue_mail_notifications'))
    CREATE INDEX idx_wolverine_queue_mail_notifications_timestamp ON fourdev_mail_transport.wolverine_queue_mail_notifications (timestamp);
IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'fourdev_mail_transport' )
    EXEC('CREATE SCHEMA [fourdev_mail_transport]');
IF OBJECT_ID('fourdev_mail_transport.wolverine_queue_mail_notifications_scheduled') IS NULL
BEGIN
CREATE TABLE fourdev_mail_transport.wolverine_queue_mail_notifications_scheduled (
    id                uniqueidentifier    NOT NULL,
    body              varbinary(max)      NOT NULL,
    message_type      varchar(250)        NOT NULL,
    execution_time    datetimeoffset      NOT NULL,
    keep_until        datetimeoffset      NULL,
    timestamp         datetimeoffset      NULL DEFAULT SYSDATETIMEOFFSET(),
CONSTRAINT pkey_wolverine_queue_mail_notifications_scheduled_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_queue_mail_notifications_scheduled_execution_time' AND object_id = OBJECT_ID(N'fourdev_mail_transport.wolverine_queue_mail_notifications_scheduled'))
    CREATE INDEX idx_wolverine_queue_mail_notifications_scheduled_execution_time ON fourdev_mail_transport.wolverine_queue_mail_notifications_scheduled (execution_time);

GO
