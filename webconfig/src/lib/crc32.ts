// CRC-32/ISO-HDLC (zlib/PNG). Matches the firmware implementation in
// mapping.cpp so the web app can validate preset payloads locally.
// Polynomial 0xEDB88320 reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF.

let TABLE: Uint32Array | null = null

function table(): Uint32Array {
  if (TABLE) return TABLE
  const t = new Uint32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    t[n] = c >>> 0
  }
  TABLE = t
  return t
}

export function crc32(bytes: Uint8Array): number {
  const t = table()
  let crc = 0xffffffff
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i] ?? 0
    const entry = t[(crc ^ b) & 0xff] ?? 0
    crc = (entry ^ (crc >>> 8)) >>> 0
  }
  return (crc ^ 0xffffffff) >>> 0
}
