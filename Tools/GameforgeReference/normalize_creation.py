"""Remove the inherited veteran path and private-server gifts from the local character-creation procedure."""
import argparse
import hashlib
import json
from pathlib import Path
import re

from sql_snapshot import sql


def hash_sql(value):return hashlib.sha256(value.replace('\r','').encode('utf-16le')).hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sql",type=Path,required=True);parser.add_argument("--evidence",type=Path,required=True)
    args=parser.parse_args()
    before=sql("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.TCreateChar')) AS body FOR JSON PATH;")[0]["body"]
    # Normalize before writing a Windows text file; otherwise legacy CR/CRLF mixtures acquire extra blank lines.
    after,count=re.subn(r'CREATE\s+PROCEDURE\s+\[dbo\]\.\[TCreateChar\]',"ALTER PROCEDURE [dbo].[TCreateChar]",before.replace('\r',''),count=1,flags=re.I)
    if count!=1:raise ValueError("Unexpected character-creation procedure declaration")
    after,count=re.subn(r'IF\(@bLevelOption != 0\).*?ELSE IF\(@bCountry <> 4\)\s*RETURN 4',
                       "-- All new characters use the neutral level-one path. The legacy option parameter is ignored.\n\tSET @bCountry = 4",after,count=1,flags=re.S)
    if count!=1:raise ValueError("Unexpected inherited veteran branch")
    after,count=re.subn(r'--\s*편지보내기[^\n]*.*?(?=\s*UPDATE TGLOBAL_GSP\.DBO\.TALLCHARTABLE)',
                       "-- Inherited 4StoryPW welcome mail removed.\n",after,count=1,flags=re.S)
    if count!=1:raise ValueError("Unexpected inherited private welcome-mail block")
    after,count=re.subn(r'\s*DECLARE @szMountName VARCHAR\(50\).*?INSERT INTO TPETTABLE[^\n]*',
                       "\n\t-- No automatic account-mount deletion or grant during character creation.\n",after,count=1,flags=re.S)
    if count!=1:raise ValueError("Unexpected inherited permanent account-mount block")
    if any(token in after for token in ("Welcome to 4StoryPW!","TVETERANCHART","DELETE TPETTABLE","INSERT INTO TPETTABLE")):
        raise ValueError("An inherited custom behavior remains")
    # SQL Server persists an ALTERed procedure's module text with a CREATE declaration.
    stored_after=after.replace("ALTER PROCEDURE [dbo].[TCreateChar]","CREATE PROCEDURE [dbo].[TCreateChar]",1)
    old_hash,new_hash=hash_sql(before),hash_sql(stored_after)
    escaped=after.replace("'","''")
    script=f"""-- Remove the identified inherited private-server creation behavior; not a claim of a full official stored procedure.
-- Existing accounts, characters, pets, mail and starter charts are retained.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
DECLARE @hash VARCHAR(64)=CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TCreateChar')),CHAR(13),'')),2);
IF @hash IS NULL OR @hash NOT IN('{old_hash}','{new_hash}') THROW 51000,'Character creation procedure baseline changed',1;
IF @hash='{old_hash}' EXEC sys.sp_executesql N'{escaped}';
IF CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TCreateChar')),CHAR(13),'')),2)<>'{new_hash}'
    THROW 51000,'Character creation procedure postcondition failed',1;
COMMIT TRANSACTION;
GO
"""
    args.sql.write_text(script,encoding="utf-8")
    args.evidence.write_text(json.dumps({"procedure":"dbo.TCreateChar","baseline_sha256_without_cr":old_hash,"normalized_sha256_without_cr":new_hash,
        "changes":["Ignore legacy level-selection option; all new characters start at level 1 in neutral country 4",
                   "Remove branded 4StoryPW welcome mail", "Remove automatic deletion/replacement of account mount 2"],
        "retained":"Existing characters/pets/mail and all unverified starter skill/item/recall charts"},indent=2)+"\n",encoding="utf-8")
    print("Prepared guarded character-creation normalization")


if __name__=="__main__":main()
