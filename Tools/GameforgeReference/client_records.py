"""Strict chart readers and lossless named-field conversions for the selected original client."""
import collections
from dataclasses import dataclass,field
from pathlib import Path
import re
import struct

from client_facts import Reader
from compare import SCHEMAS,table


@dataclass
class Record:
    key: object
    raw: bytes
    names: list=field(default_factory=list)
    extra: dict=field(default_factory=dict)


def fixed(data,size,key_size=2,group=False,key=None):
    reader=Reader(data);rows=[]
    for _ in range(reader.u16()):
        raw=reader.take(size)
        ident=key(raw) if key else int.from_bytes(raw[:key_size],'little')
        rows.append(Record(ident,raw))
    reader.done()
    if not group and len({r.key for r in rows})!=len(rows):raise ValueError("Duplicate fixed-chart keys")
    return rows


def named(path):
    data=path.read_bytes();_,meta=table(path)
    parsed=re.fullmatch(r'prefix=(\d+); strings=(\d+); suffix=(\d+); key=(\d+); key_after=(True|False)',meta['layout'])
    if not parsed:raise ValueError("Not a validated named chart")
    prefix,strings,suffix,key_size=map(int,parsed.groups()[:4]);key_after=parsed[5]=='True'
    reader=Reader(data);rows=[]
    for _ in range(reader.u16()):
        start=reader.p;head=reader.take(prefix);names=[reader.string() for _ in range(strings)];tail=reader.take(suffix)
        ident=int.from_bytes((tail if key_after else head)[:key_size],'little')
        if path.name.lower()=='thelp.tcd':ident=(ident,int.from_bytes(head[4:6],'little'))
        rows.append(Record(ident,data[start:reader.p],names))
    reader.done();return rows,meta['layout']


def info(data):
    reader=Reader(data);rows=[]
    for _ in range(reader.u16()):
        start=reader.p;ident=reader.u32();names=[reader.string() for _ in range(reader.u8())]
        rows.append(Record(ident,data[start:reader.p],names))
    reader.done();return rows


def quest_positions(data):
    reader=Reader(data);rows=[]
    for _ in range(reader.u32()):
        start=reader.p;ident=reader.u32();count=reader.u32()
        if count>10000:raise ValueError("Too many quest positions")
        reader.take(count*29);rows.append(Record(ident,data[start:reader.p]))
    reader.done();return rows


def quests(data,retail):
    reader=Reader(data);classes=[];rows=[];max_text=0
    def text():
        nonlocal max_text
        start=reader.p;value=reader.string(True);max_text=max(max_text,reader.p-start-4);return value
    count=reader.i32()
    if not 0<=count<10000:raise ValueError("Invalid quest class count")
    for _ in range(count):
        start=reader.p;ident=reader.u32();name=text();reader.u8();classes.append(Record(ident,data[start:reader.p],[name]))
    while reader.p<len(data):
        start=reader.p;parent=reader.u32() if retail else None
        ident=reader.u32();kind=reader.u8();cls=reader.u32();main=reader.u8();flag=reader.u8() if retail else None
        names=[text() for _ in range(8)];level=reader.u8();count=reader.i32();terms=[]
        if not 0<=count<1000:raise ValueError("Invalid quest terms")
        for _ in range(count):
            terms.append((reader.u8(),reader.u32(),reader.u8()));text();text()
        count=reader.i32()
        if not 0<=count<1000:raise ValueError("Invalid quest rewards")
        rewards=[struct.unpack('<BIBBB',reader.take(8)) for _ in range(count)];reader.take(8)
        raw=data[start:reader.p]
        if retail:raw=raw[4:14]+raw[15:]
        rows.append(Record(ident,raw,names,{'type':kind,'class':cls,'main':main,'level':level,'terms':terms,
                    'rewards':rewards,'source_parent':parent,'source_flag':flag}))
    reader.done()
    if max_text>1536:raise ValueError("Quest text exceeds the current native reader's buffer")
    if len({r.key for r in rows})!=len(rows):raise ValueError("Duplicate quest ID")
    return classes,rows,max_text


def skill_layouts():
    source=(Path(__file__).resolve().parents[2]/'Lib/Own/TChart/TChart/TChart.cpp').read_text(encoding='utf-8-sig')
    old_start=source.index('//void CTChart::InitTSKILLTEMP')
    actual=source.index('\nvoid CTChart::InitTSKILLTEMP',old_start)
    old=source[old_start:actual];new=source[actual:];new=new[:new.index('if(pTSKILL->m_wSkillID')]
    def fields(code):
        values=re.findall(r'>>\s*(?:pTSKILL->)?([A-Za-z_][A-Za-z_0-9]*(?:\[[^\]]+\])?)',code)
        values=values[values.index('m_wSkillID'):]
        result=[]
        for name in values:
            if name=='m_strNAME':continue
            size=1 if name=='unused' or name.startswith('m_b') else 2 if name.startswith('m_w') or name=='wWORD' else 4
            result.append((name,size))
        return result
    before,after=fields(old),fields(new)
    if sum(n for _,n in before)!=146 or sum(n for _,n in after)!=166:raise ValueError("The documented skill loader changed")
    return before,after


def skills(data,retail):
    old,new=skill_layouts();reader=Reader(data);rows=[]
    for _ in range(reader.u16()):
        start=reader.p
        if retail:
            ident=reader.u16();name_start=reader.p;name=reader.string();name_bytes=data[name_start:reader.p]
            tail=reader.take(144);number_bytes=struct.pack('<H',ident)+tail;layout=old
        else:
            name_start=reader.p;name=reader.string();name_bytes=data[name_start:reader.p]
            number_bytes=reader.take(166);ident=int.from_bytes(number_bytes[:2],'little');layout=new
        values={};unused=[];position=0
        for key,size in layout:
            value=number_bytes[position:position+size];position+=size
            if key=='unused':unused.append(value.hex())
            else:values[key]=value
        if retail:
            raw=name_bytes+b''.join(values.get(key,bytes(size)) for key,size in new)
            # All represented original fields must survive exactly, including floating-point bit patterns.
            converted={};offset=0
            for key,size in new:converted[key]=raw[len(name_bytes)+offset:len(name_bytes)+offset+size];offset+=size
            if any(converted[key]!=value for key,value in values.items()):raise ValueError("A named original skill field changed")
        else:raw=data[start:reader.p]
        rows.append(Record(ident,raw,[name],{'fields':{key:value.hex() for key,value in values.items() if key.startswith('m_')},
                                          'discarded_source_padding':unused}))
    reader.done();return rows


def quest_items(data,retail):
    size=35 if retail else 29
    count=int.from_bytes(data[:2],'little')
    if len(data)==2+count*35:size=35
    rows=fixed(data,size,4)
    if size==29:
        for row in rows:row.raw+=bytes(6)
    return rows


FIXED={
    'titemvisual.tcd':(63,2,False,None),'tnpctemp.tcd':(54,2,False,None),
    'tmap.tcd':(46,4,False,None),'tminimap.tcd':(27,4,False,None),
    'tdest.tcd':(21,4,True,None),'tfog.tcd':(37,4,True,None),
    'tlight.tcd':(40,4,False,None),'tsky.tcd':(32,4,False,None),
    'tunitlink.tcd':(10,4,False,lambda raw:raw.hex()),'titemgrade.tcd':(2,1,False,None),
    'tskillfunction.tcd':(11,2,True,None),'tskillpoint.tcd':(6,2,True,None),
    'tskilltree.tcd':(6,4,False,None),'tsfx.tcd':(12,4,False,None),
    'tstep.tcd':(8,4,True,None),'tpopupmenu.tcd':(8,4,True,lambda raw:raw.hex()),
    'tarrowdir.tcd':(6,2,True,None),
}


def merge(existing,incoming):
    present={r.key for r in existing};added=[r for r in incoming if r.key not in present]
    return existing+added,added


def serialize(rows,word_count=True):
    maximum=65535 if word_count else 1000000
    if len(rows)>maximum:raise ValueError("Chart count exceeds its native count type")
    return struct.pack('<H' if word_count else '<I',len(rows))+b''.join(row.raw for row in rows)
