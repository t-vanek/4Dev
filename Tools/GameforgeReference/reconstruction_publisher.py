"""Import existing publisher evidence archives without silently refreshing or mixing versions."""
import hashlib
import json
import os
import sqlite3
from pathlib import Path
import zlib

from reconstruction_store import key,row
from reconstruction_client import read_stable


def publisher_rows(folder,work,known=None):
    known=set(known or [])
    for path in sorted(folder.iterdir()):
        if path.suffix=='.json' and path.name.endswith('-catalogue.json'):
            data=read_stable(path);digest=hashlib.sha256(data).hexdigest();source=key('publisher_archive',path.name,digest)
            if source in known:continue
            document=json.loads(data.decode('utf-8-sig'));kind=path.name.split('-')[0]
            yield row('source.publisher',source,path.name,dict(path=str(path),sha256=digest,
                status='cached_publisher_evidence',current_server_version_match_verified=False),data)
            for ordinal,record in enumerate(document.get('records',[])):
                yield row('publisher.catalogue.'+kind,source,str(record.get('id',ordinal)),record)
            yield row('import.publisher_complete',source,path.name,dict(source_sha256=digest))
        elif path.suffix=='.sqlite':
            # backup() includes committed WAL contents and gives a consistent immutable input.
            work.mkdir(parents=True,exist_ok=True)
            snapshot=work/(path.stem+'.'+str(os.getpid())+'.snapshot.sqlite')
            # A fresh target keeps the backup file identity stable across repeated imports.
            # This is a tool-owned file directly inside the explicit snapshot directory.
            if snapshot.exists():snapshot.unlink()
            original=sqlite3.connect(path.resolve().as_uri()+'?mode=ro',uri=True)
            target=sqlite3.connect(snapshot)
            try:original.backup(target)
            finally:target.close();original.close()
            data=read_stable(snapshot);digest=hashlib.sha256(data).hexdigest();source=key('publisher_archive',path.name,digest)
            if source in known:continue
            yield row('source.publisher',source,path.name,dict(path=str(path),sha256=digest,
                status='consistent_cached_publisher_database',current_server_version_match_verified=False),data)
            database=sqlite3.connect(snapshot)
            try:
                tables={r[0] for r in database.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                if 'details' in tables:
                    columns=[r[1] for r in database.execute('PRAGMA table_info(details)')]
                    supported={'kind','id','url','sha256','facts'}<=set(columns)
                    query='SELECT kind,id,url,sha256,facts FROM details' if supported else 'SELECT NULL,NULL,NULL,NULL,NULL WHERE 0'
                    for kind,ident,url,page_hash,facts in database.execute(query):
                        if isinstance(facts,bytes):
                            try:facts=zlib.decompress(facts)
                            except zlib.error:pass
                            facts=facts.decode('utf8')
                        try:parsed=json.loads(facts)
                        except (ValueError,TypeError):parsed=dict(unparsed_facts=facts)
                        yield row('publisher.detail.'+kind,source,str(ident),dict(id=ident,url=url,
                            page_sha256=page_hash,facts=parsed,evidence_class='cached_publisher_detail'))
            finally:database.close()
            yield row('import.publisher_complete',source,path.name,dict(source_sha256=digest))
