-- Run READ-ONLY against the reference instance with sqlcmd -m 1 -y 0. Output is DDL, no player rows.
-- Execute the output ONLY in a disposable SQL instance with neither game database already present.
USE master;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
DECLARE @objects table(db sysname, kind char(1), name sysname);
INSERT @objects SELECT N'TGame_gsp','T',value FROM STRING_SPLIT(N'TCHARTABLE,TSKILLTABLE,TSKILLMAINTAINTABLE,TPVPOINTTABLE,TPVPRECORDTABLE,TMONTHPVPOINTTABLE,TTITLETABLE,TRECALLMONTABLE,TRECALLMAINTAINTABLE,TEXPITEMTABLE,TQUESTTABLE,TQUESTTERMTABLE,TINVENTABLE,TITEMTABLE,TTEMPINVENTABLE,TTEMPITEMTABLE,THOTKEYTABLE,TPETTABLE,TCOMPANIONTABLE,TCOMPANIONITEMTABLE,TLASTCOMPANIONTABLE,TMEDALS,TQTITLECHART,TRESERVEDPOST',',');
INSERT @objects SELECT N'TGlobal_gsp','T',value FROM STRING_SPLIT(N'TGROUP,TDURINGITEMTABLE,TPCBANGPLAYTABLE',',');
INSERT @objects SELECT N'TGame_gsp','P',value FROM STRING_SPLIT(N'TSaveChar,TSavePvPRecord,TSaveMonthPvPoint,TClearCharTitle,TSaveCharTitle,TSaveSkillMaintain,TSaveItemDataStart,TSaveItemDataEnd,TSaveInven,TSaveItem,TSaveQuest,TSaveQuestTerm,TSaveHotkey,TSavePet,TSaveCompanion,TSaveLastCompanion,TSaveMedals,TGetLastCompanion,TGetMedals,TSaveItemDirect',',');
INSERT @objects VALUES(N'TGlobal_gsp','P',N'TSetPcBangData');
INSERT @objects SELECT N'TGame_gsp','P',value FROM STRING_SPLIT(N'TGetPvPRecord,TGetMonthPvPoint',',');
-- Territory result procedures are tested against native shapes, without copying live rows.
INSERT @objects SELECT N'TGame_gsp','T',value FROM STRING_SPLIT(N'TLOCALTABLE,TCASTLETABLE,TMISSIONTABLE,TSKYGARDENTABLE,TLOCALOCCUPYTABLE,TCASTLEAPPLICANTTABLE,TGUILDTABLE,TGUILDMEMBERTABLE,TBATTLETIMECHART,TAIDTABLE,TBATTLEZONECHART',',');
INSERT @objects SELECT N'TGame_gsp','P',value FROM STRING_SPLIT(N'TSaveLocalOccupy,TSaveCastleOccupy,TSaveMissionOccupy,TSaveSkyGardenOccupy,TSaveCastleApplicant',',');
INSERT @objects SELECT N'TGame_gsp','T',value FROM STRING_SPLIT(N'TPOSTTABLE,TPOSTERRORTABLE,TDBITEMINDEXTABLE',',');
INSERT @objects SELECT N'TGame_gsp','P',value FROM STRING_SPLIT(N'TSavePost,TGenerateDBItemID,TInitGenItemID',',');
INSERT @objects SELECT N'TGame_gsp','P',value FROM STRING_SPLIT(N'TPostCanSend,TGetPostInfo,TPostView,TPostDelete,TPostGetItem,TPostBillsUpdate,TGetReservedPost',',');
INSERT @objects VALUES(N'TGame_gsp','T',N'TPROTECTEDTABLE');
INSERT @objects VALUES(N'TGame_gsp','P',N'TItemDelete');
DECLARE @out table(id int IDENTITY, script nvarchar(max));
DECLARE @go nvarchar(10)=CHAR(10)+N'GO'+CHAR(10);
INSERT @out VALUES(N'IF DB_ID(N''TGame_gsp'') IS NOT NULL OR DB_ID(N''TGlobal_gsp'') IS NOT NULL THROW 51042,''Fresh disposable SQL instance required'',1;'+@go);
INSERT @out SELECT N'CREATE DATABASE '+QUOTENAME(name)+N' COLLATE '+collation_name+N';'+@go
    FROM sys.databases WHERE name IN (N'TGame_gsp',N'TGlobal_gsp') ORDER BY name;
DECLARE @db sysname,@kind char(1),@name sysname,@body nvarchar(max),@sql nvarchar(max);
DECLARE objects CURSOR LOCAL FAST_FORWARD FOR SELECT db,kind,name FROM @objects ORDER BY kind DESC,db,name;
OPEN objects; FETCH NEXT FROM objects INTO @db,@kind,@name;
WHILE @@FETCH_STATUS=0
BEGIN
    SET @body=NULL;
    IF @kind='T'
    BEGIN
        SET @sql=N'USE '+QUOTENAME(@db)+N';
SELECT @body=STUFF((SELECT N'',''+QUOTENAME(c.name)+N'' ''+
 CASE WHEN c.is_computed=1 THEN N''AS ''+cc.definition+CASE WHEN cc.is_persisted=1 THEN N'' PERSISTED'' ELSE N'''' END
 ELSE TYPE_NAME(c.system_type_id)+
 CASE WHEN TYPE_NAME(c.system_type_id) IN (''varchar'',''nvarchar'',''char'',''nchar'',''varbinary'',''binary'')
 THEN N''(''+CASE WHEN c.max_length=-1 THEN N''max'' ELSE CONVERT(nvarchar(10),c.max_length/CASE WHEN TYPE_NAME(c.system_type_id) IN (''nvarchar'',''nchar'') THEN 2 ELSE 1 END) END+N'')''
 WHEN TYPE_NAME(c.system_type_id) IN (''decimal'',''numeric'') THEN N''(''+CONVERT(nvarchar(10),c.precision)+N'',''+CONVERT(nvarchar(10),c.scale)+N'')''
 WHEN TYPE_NAME(c.system_type_id) IN (''datetime2'',''datetimeoffset'',''time'') THEN N''(''+CONVERT(nvarchar(10),c.scale)+N'')'' ELSE N'''' END+
 CASE WHEN c.collation_name IS NOT NULL THEN N'' COLLATE ''+c.collation_name ELSE N'''' END+
 CASE WHEN c.is_identity=1 THEN N'' IDENTITY(''+CONVERT(nvarchar(30),ic.seed_value)+N'',''+CONVERT(nvarchar(30),ic.increment_value)+N'')'' ELSE N'''' END+
 CASE WHEN c.is_nullable=1 THEN N'' NULL'' ELSE N'' NOT NULL'' END+
 CASE WHEN dc.definition IS NOT NULL THEN N'' DEFAULT ''+dc.definition ELSE N'''' END END
FROM sys.columns c LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id WHERE c.object_id=OBJECT_ID(@name) ORDER BY c.column_id FOR XML PATH(''''),TYPE).value(''.'',''nvarchar(max)''),1,1,N'''');
DECLARE @pk nvarchar(max);
SELECT @pk=STUFF((SELECT N'',''+QUOTENAME(c.name)
FROM sys.key_constraints k JOIN sys.index_columns ix ON ix.object_id=k.parent_object_id AND ix.index_id=k.unique_index_id
JOIN sys.columns c ON c.object_id=ix.object_id AND c.column_id=ix.column_id WHERE k.type=''PK'' AND k.parent_object_id=OBJECT_ID(@name) ORDER BY ix.key_ordinal FOR XML PATH(''''),TYPE).value(''.'',''nvarchar(max)''),1,1,N'''');
IF @pk IS NOT NULL SET @body=@body+N'', PRIMARY KEY (''+@pk+N'')'';';
        EXEC sys.sp_executesql @sql,N'@name sysname,@body nvarchar(max) OUTPUT',@name,@body OUTPUT;
        IF @body IS NULL THROW 51042,'Missing source table',1;
        SET @body=N'CREATE TABLE dbo.'+QUOTENAME(@name)+N'('+@body+N');';
    END
    ELSE
    BEGIN
        SET @sql=N'USE '+QUOTENAME(@db)+N'; SELECT @body=definition FROM sys.sql_modules WHERE object_id=OBJECT_ID(@name);';
        EXEC sys.sp_executesql @sql,N'@name sysname,@body nvarchar(max) OUTPUT',@name,@body OUTPUT;
        IF @body IS NULL THROW 51042,'Missing source procedure',1;
    END;
    INSERT @out VALUES(N'USE '+QUOTENAME(@db)+N';'+@go+N'SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;'+@go+@body+@go);
    FETCH NEXT FROM objects INTO @db,@kind,@name;
END;
CLOSE objects; DEALLOCATE objects;
-- Synthetic character only. Defaults are retained; no source row is queried or exported.
DECLARE @cols nvarchar(max),@values nvarchar(max);
SELECT @cols=STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(c.name)),N',') WITHIN GROUP(ORDER BY c.column_id),
 @values=STRING_AGG(CONVERT(nvarchar(max),CASE c.name WHEN 'dwCharID' THEN N'1' WHEN 'dwUserID' THEN N'9' WHEN 'szName' THEN N'''FullFixture'''
 WHEN 'bStatLevel' THEN N'2' WHEN 'bStatPoint' THEN N'3' WHEN 'dwStatExp' THEN N'4'
 ELSE CASE WHEN c.default_object_id<>0 THEN N'DEFAULT' WHEN c.is_identity=1 THEN N'1' WHEN c.is_nullable=1 THEN N'NULL'
 WHEN TYPE_NAME(c.system_type_id) IN ('varchar','nvarchar','char','nchar','text','ntext') THEN N''''''
 WHEN TYPE_NAME(c.system_type_id) IN ('datetime','smalldatetime','datetime2','date') THEN N'''19000101'''
 WHEN TYPE_NAME(c.system_type_id)='uniqueidentifier' THEN N'''00000000-0000-0000-0000-000000000001'''
 WHEN TYPE_NAME(c.system_type_id) IN ('binary','varbinary','image') THEN N'0x' ELSE N'0' END END),N',') WITHIN GROUP(ORDER BY c.column_id)
FROM TGame_gsp.sys.columns c WHERE c.object_id=OBJECT_ID(N'TGame_gsp.dbo.TCHARTABLE') AND c.is_computed=0 AND c.system_type_id<>189;
INSERT @out VALUES(N'USE [TGame_gsp];'+@go+
 CASE WHEN EXISTS(SELECT 1 FROM TGame_gsp.sys.identity_columns WHERE object_id=OBJECT_ID(N'TGame_gsp.dbo.TCHARTABLE')) THEN N'SET IDENTITY_INSERT TCHARTABLE ON;' ELSE N'' END+
 N'INSERT TCHARTABLE('+@cols+N') VALUES('+@values+N');'+
 CASE WHEN EXISTS(SELECT 1 FROM TGame_gsp.sys.identity_columns WHERE object_id=OBJECT_ID(N'TGame_gsp.dbo.TCHARTABLE')) THEN N'SET IDENTITY_INSERT TCHARTABLE OFF;' ELSE N'' END+@go+
 N'EXEC sys.sp_addextendedproperty @name=N''4DevDisposableFixture'',@value=N''full-save-v1'';'+@go);
SELECT script FROM @out ORDER BY id;
