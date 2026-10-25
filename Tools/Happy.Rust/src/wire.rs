//! Strict little-endian TNetLib manager codec. See TControl.Protocol/Packet*.cs.
use serde_json::{Map, Value, json};
use std::io::{self, Read};

pub type Result<T> = std::result::Result<T, String>;
pub const HEADER: usize = 16;
pub const BASE: u16 = 0x9301;

#[derive(Clone, Debug, PartialEq)]
pub struct Packet {
    pub id: u16,
    pub body: Vec<u8>,
}
impl Packet {
    pub fn new(offset: u16, body: Vec<u8>) -> Self {
        Self {
            id: BASE + offset,
            body,
        }
    }
    pub fn bytes(&self) -> Result<Vec<u8>> {
        let size =
            u16::try_from(HEADER + self.body.len()).map_err(|_| "Packet exceeds 65535 bytes")?;
        let mut out = vec![0; HEADER];
        out[..2].copy_from_slice(&size.to_le_bytes());
        out[2..4].copy_from_slice(&self.id.to_le_bytes());
        out.extend_from_slice(&self.body);
        Ok(out)
    }
    pub fn read(stream: &mut impl Read) -> io::Result<Self> {
        let mut header = [0; HEADER];
        stream.read_exact(&mut header)?;
        let size = u16::from_le_bytes([header[0], header[1]]) as usize;
        if size < HEADER {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Invalid packet length",
            ));
        }
        let mut body = vec![0; size - HEADER];
        stream.read_exact(&mut body)?;
        Ok(Self {
            id: u16::from_le_bytes([header[2], header[3]]),
            body,
        })
    }
}

/// Incremental framing retains partial headers/bodies across socket read timeouts.
#[derive(Default)]
pub struct Framer(Vec<u8>);
impl Framer {
    pub fn push(&mut self, data: &[u8]) {
        self.0.extend_from_slice(data);
    }
    pub fn packet(&mut self) -> Result<Option<Packet>> {
        if self.0.len() < 2 {
            return Ok(None);
        }
        let size = u16::from_le_bytes([self.0[0], self.0[1]]) as usize;
        if size < HEADER {
            return Err("Invalid packet length".into());
        }
        if self.0.len() < size {
            return Ok(None);
        }
        let packet = Packet {
            id: u16::from_le_bytes([self.0[2], self.0[3]]),
            body: self.0[HEADER..size].to_vec(),
        };
        self.0.drain(..size);
        Ok(Some(packet))
    }
}

#[derive(Clone)]
pub enum Kind {
    U8,
    U16,
    U32,
    U64,
    I64,
    F32,
    Text,
    Manager,
    Fixed(u8),
    Record(Vec<Field>),
    List(Box<Kind>, Vec<Field>),
}
#[derive(Clone)]
pub struct Field {
    pub name: &'static str,
    pub kind: Kind,
    pub initial: Value,
}
pub fn field(name: &'static str, kind: Kind) -> Field {
    let initial = match &kind {
        Kind::Text => json!(""),
        Kind::Record(fields) => defaults(fields),
        Kind::List(..) => json!([]),
        Kind::Fixed(n) => json!(n),
        _ => json!(0),
    };
    Field {
        name,
        kind,
        initial,
    }
}
impl Field {
    pub fn with(mut self, value: impl Into<Value>) -> Self {
        self.initial = value.into();
        self
    }
}
pub fn defaults(fields: &[Field]) -> Value {
    Value::Object(
        fields
            .iter()
            .map(|f| (f.name.to_owned(), f.initial.clone()))
            .collect(),
    )
}
fn scalar<T: std::str::FromStr>(v: &Value) -> Result<T> {
    let s = match v {
        Value::String(s) => s.clone(),
        _ => v.to_string(),
    };
    s.parse().map_err(|_| format!("Invalid number: {s}"))
}
pub fn latin1(s: &str) -> Result<Vec<u8>> {
    s.chars()
        .map(|c| {
            u8::try_from(c as u32)
                .map_err(|_| format!("Character {c:?} is outside the server's Latin-1 encoding"))
        })
        .collect()
}

pub fn encode(fields: &[Field], value: &Value, manager: u32) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    write_fields(fields, value, manager, &mut out)?;
    if out.len() > u16::MAX as usize - HEADER {
        return Err("Packet exceeds 65535 bytes".into());
    }
    Ok(out)
}
fn write_fields(fields: &[Field], value: &Value, manager: u32, out: &mut Vec<u8>) -> Result<()> {
    let object = value.as_object().ok_or("Expected an object")?;
    for key in object.keys() {
        if !fields.iter().any(|f| f.name == key) {
            return Err(format!("Unknown field: {key}"));
        }
    }
    for f in fields {
        let v = match object.get(f.name) {
            Some(v) => v,
            None if matches!(f.kind, Kind::Manager | Kind::Fixed(_)) => &f.initial,
            None => return Err(format!("Missing field: {}", f.name)),
        };
        write_kind(&f.kind, v, manager, out).map_err(|e| format!("{}: {e}", f.name))?;
    }
    Ok(())
}
fn write_kind(kind: &Kind, v: &Value, manager: u32, out: &mut Vec<u8>) -> Result<()> {
    match kind {
        Kind::U8 => out.push(scalar::<u8>(v)?),
        Kind::U16 => out.extend(scalar::<u16>(v)?.to_le_bytes()),
        Kind::U32 => out.extend(scalar::<u32>(v)?.to_le_bytes()),
        Kind::U64 => out.extend(scalar::<u64>(v)?.to_le_bytes()),
        Kind::I64 => out.extend(scalar::<i64>(v)?.to_le_bytes()),
        Kind::F32 => {
            let n = scalar::<f32>(v)?;
            if !n.is_finite() {
                return Err("Expected a finite coordinate".into());
            }
            out.extend(n.to_le_bytes());
        }
        Kind::Manager => out.extend(manager.to_le_bytes()),
        Kind::Fixed(n) => out.push(*n),
        Kind::Text => {
            let bytes = latin1(v.as_str().ok_or("Expected text")?)?;
            out.extend(
                i32::try_from(bytes.len())
                    .map_err(|_| "Text is too long")?
                    .to_le_bytes(),
            );
            out.extend(bytes);
        }
        Kind::Record(fields) => write_fields(fields, v, manager, out)?,
        Kind::List(count, fields) => {
            let rows = v.as_array().ok_or("Expected an array")?;
            write_kind(count, &json!(rows.len()), manager, out)?;
            for row in rows {
                write_fields(fields, row, manager, out)?;
            }
        }
    }
    if out.len() > u16::MAX as usize - HEADER {
        return Err("Packet exceeds 65535 bytes".into());
    }
    Ok(())
}

pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}
impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }
    pub fn remaining(&self) -> usize {
        self.data.len() - self.pos
    }
    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        if n > self.remaining() {
            return Err("Truncated packet".into());
        }
        let data = &self.data[self.pos..self.pos + n];
        self.pos += n;
        Ok(data)
    }
    pub fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    pub fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }
    pub fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    pub fn i64(&mut self) -> Result<i64> {
        Ok(i64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }
    pub fn f32(&mut self) -> Result<f32> {
        let f = f32::from_le_bytes(self.take(4)?.try_into().unwrap());
        if f.is_finite() {
            Ok(f)
        } else {
            Err("Non-finite coordinate".into())
        }
    }
    pub fn text(&mut self) -> Result<String> {
        let n = self.u32()? as usize;
        Ok(self.take(n)?.iter().map(|&b| char::from(b)).collect())
    }
    pub fn finish(&self) -> Result<()> {
        if self.remaining() == 0 {
            Ok(())
        } else {
            Err(format!("{} unexpected trailing bytes", self.remaining()))
        }
    }
    pub fn fields(&mut self, fields: &[Field]) -> Result<Value> {
        let mut object = Map::new();
        for f in fields {
            object.insert(
                f.name.into(),
                self.kind(&f.kind).map_err(|e| format!("{}: {e}", f.name))?,
            );
        }
        Ok(Value::Object(object))
    }
    fn kind(&mut self, kind: &Kind) -> Result<Value> {
        Ok(match kind {
            Kind::U8 | Kind::Fixed(_) => json!(self.u8()?),
            Kind::U16 => json!(self.u16()?),
            Kind::U32 | Kind::Manager => json!(self.u32()?),
            Kind::U64 => json!(u64::from_le_bytes(self.take(8)?.try_into().unwrap())),
            Kind::I64 => json!(self.i64()?),
            Kind::F32 => json!(self.f32()?),
            Kind::Text => json!(self.text()?),
            Kind::Record(f) => self.fields(f)?,
            Kind::List(count, fields) => {
                let n = self.kind(count)?.as_u64().ok_or("Invalid array count")? as usize;
                if n > self.remaining() {
                    return Err("Array count exceeds packet body".into());
                }
                let mut rows = Vec::new();
                for _ in 0..n {
                    rows.push(self.fields(fields)?);
                }
                json!(rows)
            }
        })
    }
}
