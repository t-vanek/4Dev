"""Immutable, source-addressed reconstruction evidence in SQL Server (SQLite for tests)."""
import hashlib
import json
import re
import sqlite3
import subprocess


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'), allow_nan=False)


def key(*parts):
    return hashlib.sha256(canonical(parts).encode('utf8')).hexdigest()


def row(kind, source, entity, payload, blob=None, stable=False):
    encoded=canonical(payload)
    data_hash=key(encoded, hashlib.sha256(blob).hexdigest() if blob is not None else None)
    return dict(key=key(kind,source,str(entity)) if stable else key(kind,source,str(entity),data_hash),
        kind=kind,source=source,entity=str(entity),hash=data_hash,payload=encoded,blob=blob)


class BlobReader:
    def read_blob(self,record_key):
        record=self.get_record(record_key)
        if record is None:raise KeyError(record_key)
        blob=self.fetch_blob(record_key)
        if blob is None:
            chunks=sorted(self.rows(kind='blob.chunk',source=record_key),key=lambda r:int(r['entity']))
            if not chunks:return None
            parts=[]
            for ordinal,chunk in enumerate(chunks):
                meta=json.loads(chunk['payload'])
                value=self.fetch_blob(chunk['key'])
                if meta['ordinal']!=ordinal or value is None or hashlib.sha256(value).hexdigest()!=meta['sha256']:
                    raise ValueError('Missing or corrupt source blob chunk')
                parts.append(value)
            blob=b''.join(parts)
        expected=key(record['payload'],hashlib.sha256(blob).hexdigest())
        if expected!=record['hash']:raise ValueError('Restored source blob hash does not match immutable record')
        return blob


class SQLiteStore(BlobReader):
    """Explicit local/test backend; production collection defaults to SQL Server."""
    def __init__(self,path):
        self.db=sqlite3.connect(path)
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.executescript('''CREATE TABLE IF NOT EXISTS Evidence (
          RecordKey TEXT PRIMARY KEY, Kind TEXT NOT NULL, SourceKey TEXT NOT NULL,
          EntityKey TEXT NOT NULL, DataHash TEXT NOT NULL, Payload TEXT NOT NULL,
          Blob BLOB, CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
          CREATE INDEX IF NOT EXISTS IX_Evidence_KindSource ON Evidence(Kind,SourceKey);
          CREATE INDEX IF NOT EXISTS IX_Evidence_Entity ON Evidence(Kind,EntityKey);
        ''')

    def put(self,rows):
        before=self.db.total_changes
        with self.db:
            for r in rows:
                old=self.db.execute('SELECT DataHash FROM Evidence WHERE RecordKey=?',(r['key'],)).fetchone()
                if old and old[0]!=r['hash']:
                    raise ValueError('Conflicting immutable evidence identity: '+r['key'])
                self.db.execute('INSERT OR IGNORE INTO Evidence(RecordKey,Kind,SourceKey,EntityKey,DataHash,Payload,Blob) VALUES(?,?,?,?,?,?,?)',
                    (r['key'],r['kind'],r['source'],r['entity'],r['hash'],r['payload'],r['blob']))
        return self.db.total_changes-before

    def counts(self):
        return dict(self.db.execute('SELECT Kind,COUNT(*) FROM Evidence GROUP BY Kind'))

    def get_record(self,record_key):
        found=self.db.execute('SELECT RecordKey,Kind,SourceKey,EntityKey,DataHash,Payload FROM Evidence WHERE RecordKey=?',(record_key,)).fetchone()
        return dict(zip(('key','kind','source','entity','hash','payload'),found)) if found else None

    def fetch_blob(self,record_key):
        found=self.db.execute('SELECT Blob FROM Evidence WHERE RecordKey=?',(record_key,)).fetchone()
        return found[0] if found else None

    def rows(self,kind=None,entity=None,source=None,prefix=None):
        query='SELECT RecordKey,Kind,SourceKey,EntityKey,DataHash,Payload FROM Evidence WHERE 1=1'
        args=[]
        for column,value in [('Kind',kind),('EntityKey',entity),('SourceKey',source)]:
            if value is not None:query+=' AND '+column+'=?';args.append(str(value))
        if prefix is not None:query+=' AND Kind LIKE ?';args.append(prefix+'%')
        return [dict(zip(('key','kind','source','entity','hash','payload'),r)) for r in self.db.execute(query,args)]

    def watermarks(self,source):
        return {(int(stream),direction):int(sequence) for stream,direction,sequence in self.db.execute(
            "SELECT json_extract(Payload,'$.stream'),json_extract(Payload,'$.direction'),MAX(json_extract(Payload,'$.sequence')) FROM Evidence WHERE Kind='packet' AND SourceKey=? GROUP BY 1,2",(source,))}


class SqlServerStore(BlobReader):
    DATABASE='FourDevReference'
    def __init__(self,container='araz-mssql',initialize=True):
        if not re.fullmatch(r'[A-Za-z0-9_.-]+',container):raise ValueError('Invalid SQL container name')
        self.container=container
        if initialize:
            self.execute("IF DB_ID(N'FourDevReference') IS NULL EXEC(N'CREATE DATABASE [FourDevReference]');",database='master')
            self.execute('''IF OBJECT_ID(N'dbo.Evidence',N'U') IS NULL BEGIN
              CREATE TABLE dbo.Evidence (
                RecordKey char(64) NOT NULL CONSTRAINT PK_ReferenceEvidence PRIMARY KEY,
                Kind nvarchar(64) NOT NULL,SourceKey char(64) NOT NULL,
                EntityKey nvarchar(200) NOT NULL,DataHash char(64) NOT NULL,
                Payload nvarchar(max) NOT NULL,Blob varbinary(max) NULL,
                CreatedUtc datetime2(7) NOT NULL CONSTRAINT DF_ReferenceCreated DEFAULT SYSUTCDATETIME(),
                CONSTRAINT CK_ReferencePayloadJson CHECK(ISJSON(Payload)=1));
              CREATE INDEX IX_Reference_KindSource ON dbo.Evidence(Kind,SourceKey);
              CREATE INDEX IX_Reference_Entity ON dbo.Evidence(Kind,EntityKey);
            END;''')

    def execute(self,sql,database=None):
        # The password is expanded inside the existing container, never returned to the caller.
        command=['docker','exec','-i',self.container,'/bin/bash','-c',
            '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -x -f 65001 -y 0 -w 65535 -d "$1"',
            'reference',database or self.DATABASE]
        result=subprocess.run(command,input='SET NOCOUNT ON;\n'+sql+'\nGO\n',capture_output=True,encoding='utf8',timeout=120)
        if result.returncode:
            raise RuntimeError('Reference SQL operation failed: '+(result.stderr or result.stdout)[-1400:])
        return result.stdout

    def put(self,rows):
        if not rows:return 0
        encoded=[]
        for r in rows:
            record={k:v for k,v in r.items() if k!='blob'}
            record['blob_hex']=r['blob'].hex() if r['blob'] is not None else None
            encoded.append(record)
        value=canonical(encoded)
        # sqlcmd truncates exceptionally long input lines. Every literal stays under 2000 chars.
        statements=['DECLARE @doc nvarchar(max); DECLARE @chunks TABLE (ordinal int PRIMARY KEY,[value] nvarchar(3000));']
        chunks=[]
        for off in range(0,len(value),1500):
            chunks.append('('+str(off)+",N'"+value[off:off+1500].replace("'","''")+"')")
        for off in range(0,len(chunks),500):
            statements.append('INSERT @chunks VALUES\n'+',\n'.join(chunks[off:off+500])+';')
        statements.append("SELECT @doc=STRING_AGG(CONVERT(nvarchar(max),[value]),N'') WITHIN GROUP(ORDER BY ordinal) FROM @chunks;")
        statements.append('''SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'FourDevReference.Import',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
IF @lock<0 THROW 51000,'Reference import lock unavailable',1;
SELECT * INTO #incoming FROM OPENJSON(@doc) WITH (
 [key] char(64),kind nvarchar(64),source char(64),entity nvarchar(200),
 [hash] char(64),payload nvarchar(max),blob_hex varchar(max));
IF EXISTS(SELECT 1 FROM #incoming i JOIN dbo.Evidence e ON e.RecordKey=i.[key] WHERE e.DataHash<>i.[hash])
 THROW 51001,'Conflicting immutable evidence identity',1;
INSERT dbo.Evidence(RecordKey,Kind,SourceKey,EntityKey,DataHash,Payload,Blob)
 SELECT i.[key],i.kind,i.source,i.entity,i.[hash],i.payload,CONVERT(varbinary(max),i.blob_hex,2)
 FROM #incoming i WHERE NOT EXISTS(SELECT 1 FROM dbo.Evidence e WHERE e.RecordKey=i.[key]);
SELECT @@ROWCOUNT AS inserted FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
COMMIT;''')
        result=self.execute('\n'.join(statements))
        return int(json.loads(''.join(result.splitlines()).strip())['inserted'])

    def counts(self):
        result=''.join(self.execute('SELECT Kind AS kind,COUNT_BIG(*) AS count FROM dbo.Evidence GROUP BY Kind FOR JSON PATH;').splitlines()).strip()
        return {r['kind']:r['count'] for r in json.loads(result or '[]')}

    def get_record(self,record_key):
        if not re.fullmatch(r'[a-f0-9]{64}',record_key):raise ValueError('Invalid record key')
        result=''.join(self.execute("SELECT RecordKey AS [key],Kind AS kind,SourceKey AS source,EntityKey AS entity,DataHash AS [hash],Payload AS payload FROM dbo.Evidence WHERE RecordKey='"+record_key+"' FOR JSON PATH;").splitlines()).strip()
        rows=json.loads(result or '[]')
        return rows[0] if rows else None

    def fetch_blob(self,record_key):
        if not re.fullmatch(r'[a-f0-9]{64}',record_key):raise ValueError('Invalid record key')
        result=''.join(self.execute("SELECT CONVERT(varchar(max),Blob,2) AS blob_hex FROM dbo.Evidence WHERE RecordKey='"+record_key+"' FOR JSON PATH;").splitlines()).strip()
        rows=json.loads(result or '[]')
        return bytes.fromhex(rows[0]['blob_hex']) if rows and 'blob_hex' in rows[0] else None

    def rows(self,kind=None,entity=None,source=None,prefix=None):
        terms=[]
        for column,value in [('Kind',kind),('EntityKey',entity),('SourceKey',source)]:
            if value is not None:terms.append(column+"=N'"+str(value).replace("'","''")+"'")
        if prefix is not None:
            if not re.fullmatch(r'[a-zA-Z0-9._]+',prefix):raise ValueError('Invalid kind prefix')
            terms.append("Kind LIKE N'"+prefix.replace('_','[_]')+"%'")
        where=' WHERE '+' AND '.join(terms) if terms else ''
        result=''.join(self.execute('SELECT RecordKey AS [key],Kind AS kind,SourceKey AS source,EntityKey AS entity,DataHash AS [hash],Payload AS payload FROM dbo.Evidence'+where+' FOR JSON PATH;').splitlines()).strip()
        return json.loads(result or '[]')

    def watermarks(self,source):
        if not re.fullmatch(r'[a-f0-9]{64}',source):raise ValueError('Invalid session key')
        result=''.join(self.execute("SELECT TRY_CONVERT(int,JSON_VALUE(Payload,'$.stream')) AS stream,JSON_VALUE(Payload,'$.direction') AS direction,MAX(TRY_CONVERT(bigint,JSON_VALUE(Payload,'$.sequence'))) AS sequence FROM dbo.Evidence WHERE Kind=N'packet' AND SourceKey='"+source+"' GROUP BY JSON_VALUE(Payload,'$.stream'),JSON_VALUE(Payload,'$.direction') FOR JSON PATH;").splitlines()).strip()
        return {(r['stream'],r['direction']):r['sequence'] for r in json.loads(result or '[]')}


def batches(store,rows,size=600):
    batch=[];inserted=0;bytes_count=0
    def expandable():
        for record in rows:
            blob=record['blob']
            if blob is not None and len(blob)>4_000_000:
                # Large client UI packs exceed SQL Server's per-request packet limit.
                # Store immutable numbered chunks before publishing their parent record.
                for ordinal,off in enumerate(range(0,len(blob),512_000)):
                    chunk=blob[off:off+512_000]
                    yield row('blob.chunk',record['key'],str(ordinal),dict(parent=record['key'],ordinal=ordinal,
                        bytes=len(chunk),sha256=hashlib.sha256(chunk).hexdigest()),chunk,stable=True)
                record=dict(record,blob=None)
            yield record
    for record in expandable():
        estimate=len(record['payload'].encode('utf8'))+len(record['blob'] or b'')*2
        if batch and bytes_count+estimate>2_000_000:
            inserted+=store.put(batch);batch=[];bytes_count=0
        batch.append(record)
        bytes_count+=estimate
        if len(batch)>=size:
            inserted+=store.put(batch);batch=[];bytes_count=0
    return inserted+store.put(batch)
