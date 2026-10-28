"""Hash retained SQL tables without exporting account, inventory or message contents."""
import argparse
import json
from pathlib import Path

from sql_snapshot import sql


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database",required=True);parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--compare",type=Path)
    parser.add_argument("--exclude",nargs="*",help="Explicit tables changed by this operation; overrides historical normalization defaults")
    args=parser.parse_args()
    excluded=("TMONSTERCHART","TMONATTRCHART","TGAMEFORGEMONSTERRESTORE","TITEMCHART","TLEVELCHART","TMOUNTCHART",
              "TQUESTCHART","TQREWARDCHART","TNPCITEMCHART","TSTARTSKILL","TSTARTHOTKEY") if args.database=="TGame_gsp" else ()
    if args.exclude is not None:
        import re
        if any(not re.fullmatch(r'[A-Za-z0-9_]+',name) for name in args.exclude):
            raise ValueError('Invalid excluded table name')
        excluded=tuple(args.exclude)
    where=" AND t.name NOT IN("+",".join("N'"+name+"'" for name in excluded)+")" if excluded else ""
    query="""
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
CREATE TABLE #RetainedHashes(TableName NVARCHAR(256),[RowCount] BIGINT,Sha256 VARCHAR(64));
DECLARE @schema SYSNAME,@table SYSNAME,@object INT,@order NVARCHAR(MAX),@statement NVARCHAR(MAX);
DECLARE tables CURSOR LOCAL FAST_FORWARD FOR
SELECT s.name,t.name,t.object_id FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0
"""+where+""" ORDER BY s.name,t.name;
OPEN tables; FETCH NEXT FROM tables INTO @schema,@table,@object;
WHILE @@FETCH_STATUS=0 BEGIN
    SELECT @order=STUFF((SELECT N','+QUOTENAME(c.name) FROM sys.indexes i
      JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
      JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
      WHERE i.object_id=@object AND i.is_primary_key=1 ORDER BY ic.key_ordinal FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,1,N'');
    SET @statement=N'DECLARE @json NVARCHAR(MAX)=(SELECT * FROM '+QUOTENAME(@schema)+N'.'+QUOTENAME(@table)
       +CASE WHEN LEN(@order)>0 THEN N' ORDER BY '+@order ELSE N'' END+N' FOR JSON PATH,INCLUDE_NULL_VALUES);'
       +N' INSERT #RetainedHashes SELECT @name,COUNT_BIG(*),CONVERT(VARCHAR(64),HASHBYTES(''SHA2_256'',@json),2) FROM '
       +QUOTENAME(@schema)+N'.'+QUOTENAME(@table)+N';';
    DECLARE @name NVARCHAR(256)=@schema+N'.'+@table;
    EXEC sys.sp_executesql @statement,N'@name NVARCHAR(256)',@name=@name;
    FETCH NEXT FROM tables INTO @schema,@table,@object;
END;
CLOSE tables; DEALLOCATE tables;
SELECT TableName,[RowCount],Sha256 FROM #RetainedHashes ORDER BY TableName FOR JSON PATH;
DROP TABLE #RetainedHashes;
"""
    result=sql(query,args.database)
    if args.compare:
        before=json.loads(args.compare.read_text(encoding="utf-8"))
        if before!=result:
            lookup={r["TableName"]:r for r in before}
            changed=[row["TableName"] for row in result if lookup.get(row["TableName"])!=row]
            raise ValueError("Retained tables changed: "+", ".join(changed))
    args.output.write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"database":args.database,"retained_tables":len(result),"rows":sum(r["RowCount"] for r in result),"unchanged":bool(args.compare)}))


if __name__=="__main__":main()
