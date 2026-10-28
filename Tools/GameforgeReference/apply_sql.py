"""Execute a reviewed normalization script against an explicitly named local database."""
import argparse
from pathlib import Path
import re
import subprocess


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--file",type=Path,required=True)
    parser.add_argument("--database",required=True)
    parser.add_argument("--twice",action="store_true")
    parser.add_argument("--rollback",action="store_true")
    args=parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_]+",args.database):raise ValueError("Invalid database identifier")
    script=args.file.read_text(encoding="utf-8-sig").replace("USE [TGame_gsp];","USE ["+args.database+"];")
    if args.twice:script+="\n"+script
    if args.rollback:script="USE ["+args.database+"];\nBEGIN TRANSACTION;\nGO\n"+script+"\nROLLBACK TRANSACTION;\nGO\n"
    command=["docker","exec","-i","araz-mssql","/bin/bash","-c",
             'exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -w 220 "$@"',
             "sqlcmd","-d",args.database]
    result=subprocess.run(command,input=script,text=True,encoding="utf-8",capture_output=True)
    print(result.stdout,end="")
    if result.stderr:print(result.stderr,end="")
    raise SystemExit(result.returncode)


if __name__=="__main__":main()
