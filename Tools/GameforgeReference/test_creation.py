"""Transactionally exercise all six classes and four legacy options, retaining account pets and creating no mail."""
import argparse
import json
from pathlib import Path

from sql_snapshot import sql


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database",required=True);parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    query="""
SET XACT_ABORT ON;
DECLARE @charsBefore INT=(SELECT COUNT(*) FROM dbo.TCHARTABLE),
        @petsBefore INT=(SELECT COUNT(*) FROM dbo.TPETTABLE),
        @globalBefore INT=(SELECT COUNT(*) FROM TGLOBAL_GSP.dbo.TALLCHARTABLE);
DECLARE @class TINYINT=0,@option TINYINT,@user INT,@id INT,@count TINYINT,@result INT,@name VARCHAR(50),@country TINYINT,@tested INT=0;
BEGIN TRANSACTION;
WHILE @class<6 BEGIN
    SET @option=0;
    WHILE @option<4 BEGIN
        SET @user=-2000000000+@class*100+@option;
        SET @name='GFNormalizeCheck'+CONVERT(VARCHAR(1),@class)+CONVERT(VARCHAR(1),@option);
        SET @country=@option%2;
        IF EXISTS(SELECT 1 FROM dbo.TCHARTABLE WHERE dwUserID=@user OR szNAME=@name)
            OR EXISTS(SELECT 1 FROM dbo.TPETTABLE WHERE dwUserID=@user)
            OR EXISTS(SELECT 1 FROM TGLOBAL_GSP.dbo.TALLCHARTABLE WHERE dwUserID=@user OR szNAME=@name)
            THROW 51000,'The reserved creation-test identity is already occupied',1;
        INSERT dbo.TPETTABLE(dwUserID,wPetID,szName,timeUse,bEffect) VALUES(@user,2,'GFKeepExistingPet','20200101',0);
        EXEC @result=dbo.TCreateChar @count OUTPUT,@name,@id OUTPUT,@user,1,0,@class,0,@country,0,0,0,0,0,0,0,@option;
        IF @result<>0 OR @id=0 THROW 51000,'Character creation probe failed',1;
        IF NOT EXISTS(SELECT 1 FROM dbo.TCHARTABLE WHERE dwCharID=@id AND dwUserID=@user AND bLevel=1 AND bCountry=4 AND wMapID=2010 AND wSkillPoint=0)
            THROW 51000,'New character did not use the neutral level-one profile',1;
        IF NOT EXISTS(SELECT 1 FROM TGLOBAL_GSP.dbo.TALLCHARTABLE WHERE dwCharID=@id AND dwUserID=@user AND bLevel=1 AND bCountry=4)
            THROW 51000,'Global character profile differs',1;
        IF EXISTS(SELECT 1 FROM dbo.TPOSTTABLE WHERE dwCharID=@id) THROW 51000,'Character creation generated private-server mail',1;
        IF EXISTS(SELECT 1 FROM dbo.TSKILLTABLE owned JOIN dbo.TSKILLCHART skill ON skill.wID=owned.wSkillID WHERE owned.dwCharID=@id AND skill.bLevel>1)
            THROW 51000,'New level-one character received a higher-level skill',1;
        IF (SELECT COUNT(*) FROM dbo.TPETTABLE WHERE dwUserID=@user)<>1
            OR NOT EXISTS(SELECT 1 FROM dbo.TPETTABLE WHERE dwUserID=@user AND wPetID=2 AND szName='GFKeepExistingPet' AND timeUse='20200101')
            THROW 51000,'Character creation replaced/deleted an existing account pet',1;
        SET @tested=@tested+1;
        SET @option=@option+1;
    END;
    SET @class=@class+1;
END;
ROLLBACK TRANSACTION;
IF (SELECT COUNT(*) FROM dbo.TCHARTABLE)<>@charsBefore OR (SELECT COUNT(*) FROM dbo.TPETTABLE)<>@petsBefore
    OR (SELECT COUNT(*) FROM TGLOBAL_GSP.dbo.TALLCHARTABLE)<>@globalBefore
    THROW 51000,'Character probes did not fully roll back',1;
SELECT @tested AS tested_profiles,'All classes/options start at level 1, country 4; no mail; account pet retained; all inserted rows rolled back' AS result FOR JSON PATH;
"""
    result=sql(query,args.database)
    args.output.write_text(json.dumps({"database":args.database,"checks":result,"note":"Rollback leaves identity-number gaps; no character, mail or pet rows are retained"},indent=2)+"\n",encoding="utf-8")
    print(json.dumps(result))


if __name__=="__main__":main()
