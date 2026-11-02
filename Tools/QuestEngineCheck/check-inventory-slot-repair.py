import pathlib,subprocess,json
import argparse,re
parser=argparse.ArgumentParser(description='Synthetic, disposable SQL checks for migration 047; never use a game instance.')
parser.add_argument('--container',required=True)
parser.add_argument('--output',type=pathlib.Path,required=True)
args=parser.parse_args()
if not re.fullmatch(r'4dev-inventory-sql-[a-z0-9-]+',args.container):
 raise SystemExit('Only a separate 4dev-inventory-sql-* container is allowed')
container=args.container
p=args.output;p.mkdir(parents=True,exist_ok=True)
# CREATE DATABASE is deliberately the first SQL statement. An existing game catalog fails before any fixture writes.
scripts={
'fixture':'''CREATE DATABASE TGame_gsp;
GO
USE TGame_gsp;
CREATE TABLE dbo.TCharacterSaveQueue(Sequence bigint);
CREATE TABLE dbo.TCHARTABLE(dwCharID int PRIMARY KEY);
CREATE TABLE dbo.TITEMCHART(wItemID smallint PRIMARY KEY,bSlotCount tinyint);
CREATE TABLE dbo.TINVENTABLE(dwCharID int,bInvenID tinyint,wItemID smallint,PRIMARY KEY(dwCharID,bInvenID));
CREATE TABLE dbo.TITEMTABLE(dlID bigint PRIMARY KEY,bStorageType tinyint,dwStorageID int,bOwnerType tinyint,dwOwnerID int,bItemID tinyint,wItemID smallint,bCount tinyint,Attributes varchar(50));
INSERT dbo.TCHARTABLE VALUES(1),(2);
INSERT dbo.TITEMCHART VALUES(3,2),(100,2);
INSERT dbo.TINVENTABLE VALUES(1,255,3),(1,0,100),(2,255,3);
INSERT dbo.TITEMTABLE VALUES
 (1,0,255,0,1,0,1000,1,'first'),(2,0,255,0,1,1,1001,1,'second'),
 (3,0,255,0,1,2,9656,1,'herb'),(4,0,10,0,1,0,1200,7,'orphan'),
 (5,0,255,0,2,0,1300,3,'original'),(6,0,255,0,2,0,1400,4,'duplicate'),
 (7,2,999,0,1,250,1500,5,'mail unchanged'),(8,0,254,0,1,25,1600,1,'equipment unchanged'),
 (9,0,250,0,1,29,1700,1,'battle unchanged');
SELECT * INTO dbo.BeforeItems FROM dbo.TITEMTABLE;
''',
'check':'''USE TGame_gsp; SET NOCOUNT ON;
IF NOT EXISTS(SELECT 1 FROM TITEMTABLE WHERE dlID=3 AND dwStorageID=0 AND bItemID=0) THROW 51947,'Herb was not recovered into a visible slot',1;
IF NOT EXISTS(SELECT 1 FROM TITEMTABLE WHERE dlID=4 AND dwStorageID=0 AND bItemID=1) THROW 51947,'Orphan was not recovered',1;
IF NOT EXISTS(SELECT 1 FROM TITEMTABLE WHERE dlID=6 AND dwStorageID=255 AND bItemID=1) THROW 51947,'Duplicate was not recovered',1;
IF EXISTS(SELECT dlID,bStorageType,bOwnerType,dwOwnerID,wItemID,bCount,Attributes FROM BeforeItems EXCEPT SELECT dlID,bStorageType,bOwnerType,dwOwnerID,wItemID,bCount,Attributes FROM TITEMTABLE) OR (SELECT COUNT(*) FROM TITEMTABLE)<>(SELECT COUNT(*) FROM BeforeItems) THROW 51947,'Item identities, quantities or attributes changed',1;
IF EXISTS(SELECT * FROM BeforeItems WHERE dlID IN (7,8,9) EXCEPT SELECT * FROM TITEMTABLE WHERE dlID IN (7,8,9)) THROW 51947,'Unrelated storage changed',1;
SELECT 'PASS visible overflow/orphan/duplicate recovery; all identities/quantities/attributes and mail/equipment/battle preserved';
''',
'no-space':'''USE TGame_gsp; INSERT TITEMTABLE VALUES(10,0,255,0,2,9,2000,1,'no room');''',
'rollback-check':'''USE TGame_gsp; IF NOT EXISTS(SELECT 1 FROM TITEMTABLE WHERE dlID=10 AND dwStorageID=255 AND bItemID=9 AND Attributes='no room') OR (SELECT COUNT(*) FROM TITEMTABLE)<>10 THROW 51947,'No-space rollback lost state',1; DELETE FROM TITEMTABLE WHERE dlID=10; INSERT TCharacterSaveQueue VALUES(1);''',
'queue-check':'''USE TGame_gsp; IF (SELECT COUNT(*) FROM TCharacterSaveQueue)<>1 OR (SELECT COUNT(*) FROM TITEMTABLE)<>9 THROW 51947,'Queue refusal changed state',1; SELECT 'PASS queue refusal retains state';'''
}
log=[]
def run(name,expected=0):
 file=p/(name+'.sql'); file.write_text(scripts[name],encoding='utf-8')
 subprocess.run(['docker','cp',str(file),container+':/tmp/'+name+'.sql'],check=True,capture_output=True)
 r=subprocess.run(['docker','exec',container,'/bin/bash','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -i /tmp/'+name+'.sql'],capture_output=True,text=True)
 log.append(name+'\n'+r.stdout+r.stderr)
 assert (r.returncode==0)==(expected==0),log[-1]
 if expected: assert '51047' in r.stdout,log[-1]
def migrate(expected=0):
 r=subprocess.run(['docker','exec',container,'/bin/bash','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -i /tmp/migration.sql'],capture_output=True,text=True)
 log.append('migration\n'+r.stdout+r.stderr)
 assert (r.returncode==0)==(expected==0),log[-1]
 if expected: assert '51047' in r.stdout,log[-1]
subprocess.run(['docker','cp','Database/migrations/047_inventory_visible_slots.sql',container+':/tmp/migration.sql'],check=True,capture_output=True)
try:
 run('fixture');migrate();run('check');migrate();run('check');run('no-space');migrate(1);run('rollback-check');migrate(1);run('queue-check')
 (p/'migration-validation.json').write_text(json.dumps({'passed':['overflow item recovered to a visible slot','orphan container item recovered','duplicate slot repaired without losing either row','all item IDs/counts/attributes retained; mail/equipment/battle untouched','migration repeat makes no further changes','insufficient space rejects atomically','pending character queue rejects without modifying items'],'isolated_server':container,'live_player_data_modified':False},indent=2))
 print('7 isolated SQL recovery checks passed')
finally: (p/'migration-tests.log').write_text('\n'.join(log))
