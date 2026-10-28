"""Cross-check published item stats against the original client; map only explicit country labels."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import struct
import zlib

from identity_audit import normal
from monsters import bounds
from normalization import migration


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--facts",type=Path,required=True)
    parser.add_argument("--public",type=Path,required=True);parser.add_argument("--cache",type=Path,required=True)
    parser.add_argument("--plan",type=Path,required=True);parser.add_argument("--sql",type=Path,required=True)
    args=parser.parse_args()
    source={r["wItemID"]:r for r in load(args.facts/"items.json")}
    attrs={r["wID"]:r for r in load(args.facts/"item_attributes.json")}
    money={r["bLevel"]:r["dwMoney"] for r in load(args.facts/"level_fees.json")}
    baseline={r["wItemID"]:r for r in load(args.snapshot/"TITEMCHART.json")}
    catalogue={r["id"]:r for r in load(args.public/"item-catalogue.json")["records"]}
    with sqlite3.connect(args.public/"item-details.sqlite") as connection:
        details={ident:json.loads(zlib.decompress(blob)) for ident,blob in connection.execute("SELECT id,facts FROM details WHERE kind='item'")}
    if details.keys()!=catalogue.keys():raise ValueError("The public item detail capture is incomplete")
    changes=[];verified=[];gaps=[];contradictions=[];derived=[];field_counts=collections.Counter()
    # These three values are explicitly documented by the site's country filter and NetCode.h TCONTRY_TYPE.
    # The site's generic 'War zone' label has no unambiguous neutral/battle mapping and is retained as unknown.
    countries={"Valorián":0,"Derion":1,"Gor":4}
    for ident,published in sorted(catalogue.items()):
        original=source.get(ident);detail=details[ident]
        if not original or normal(original["szNAME"])!=normal(published["name"]):
            gaps.append({"id":ident,"reason":"Public/client item name differs"});continue
        payload=(args.cache/(hashlib.sha256(detail["url"].encode()).hexdigest()+".html")).read_bytes()
        if hashlib.sha256(payload).hexdigest()!=detail["sha256"]:raise ValueError("Changed public item evidence")
        values=detail["fields"];attribute=attrs.get(original["wAttrID"]) if original["wAttrID"] else None;checked=[];differences=[]
        def check(label,wanted,parser):
            if label not in values or values[label]=="":return
            try:actual=parser(values[label])
            except ValueError:
                differences.append({"field":label,"public":values[label],"client":wanted,"reason":"Unsupported published format"});return
            field_counts[label]+=1
            if actual!=wanted:differences.append({"field":label,"public":actual,"client":wanted})
            else:checked.append(label)
        check("Level",original["bLevel"],int)
        check("Dosah",[original["bMinRange"],original["bMaxRange"]],bounds)
        def maximum(text):
            m=re.fullmatch(r'\d+\s*/\s*(\d+)',text)
            if not m:raise ValueError("Invalid maximum")
            return int(m[1])
        check("Životnost",original["dwDuraMax"],maximum)
        check("Zušlechťování",original["bRefineMax"],maximum)
        grade=attribute["bGrade"] if attribute else original["bLevel"]
        if attribute:
            check("Poškození",[attribute["wMinAP"],attribute["wMaxAP"]],bounds)
            check("Magické poškození",[attribute["wMinMAP"],attribute["wMaxMAP"]],bounds)
            check("Obrana",attribute["wDP"],int)
            check("Fyzická obrana",attribute["wDP"],int)
            check("Magická obrana",attribute["wMDP"],int)
        price=int(struct.unpack('<f',struct.pack('<f',money[grade]*original["fPrice"]))[0]+.99)
        def currency(text):
            m=re.fullmatch(r'(\d+)\s+(\d+)\s+(\d+)',text)
            if not m:raise ValueError("Invalid gold/silver/bronze")
            return int(m[1])*1000000+int(m[2])*1000+int(m[3])
        # Website power level is derived (e.g. axe 501 publishes level 4 while raw attr grade is 1).
        # Its default display/price mapping is not the raw attribute grade. Record both without declaring
        # them contradictory or changing fPrice from an unverified interpretation.
        derived.append({"id":ident,"public_power_level":values.get("Level schopností"),"raw_attribute_grade":grade,
                        "public_price":values.get("Cena"),"sql_base_price_formula":price,
                        "status":"Derived display/default-grade mapping remains unverified"})
        record={"id":ident,"name":original["szNAME"],"verified_fields":checked,"source":detail["url"],"sha256":detail["sha256"]}
        if differences:
            record["differences"]=differences;contradictions.append(record);continue
        verified.append(record)
        labels={img.get("alt") for cell in published["cell_images"] for img in cell if img.get("alt") in countries or img.get("alt")=="Válečná oblast"}
        detail_labels={img.get("alt") for table in detail["tables"] for row in table["rows"] for cell in row for img in cell["images"]
                       if img.get("alt") in countries or img.get("alt")=="Válečná oblast"}
        if len(labels)!=1 or labels!=detail_labels:
            gaps.append({"id":ident,"reason":"Catalogue/detail country labels differ or are ambiguous"});continue
        label=next(iter(labels))
        if label not in countries:continue
        if ident not in baseline:
            gaps.append({"id":ident,"reason":"SQL template missing"});continue
        before,after=baseline[ident]["bItemCountry"],countries[label]
        if before!=after:
            changes.append({"table":"TITEMCHART","key_column":"wItemID","key":ident,"column":"bItemCountry",
                            "before":before,"after":after,"source":detail["url"],"source_sha256":detail["sha256"],"published_country":label})
    plan={"scope":"Country labels with documented numeric values, gated on matching original-client identity and all decoded published stats",
          "changes":changes,"verified_items":verified,"client_public_contradictions":contradictions,"gaps":gaps,
          "unmapped_derived_values":derived,
          "compared_fields":dict(field_counts),"unknown_country":"War zone is not assigned a numeric country without evidence"}
    args.plan.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    migration(plan,load(args.snapshot/"schema.json"),args.sql)
    print(json.dumps({"published_items":len(catalogue),"verified_items":len(verified),"contradictions":len(contradictions),
                      "country_changes":len(changes),"gaps":len(gaps),"compared_fields":dict(field_counts)}))


if __name__=="__main__":main()
