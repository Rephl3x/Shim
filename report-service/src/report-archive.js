export const MAX_UPLOAD_BYTES = 8 * 1024 * 1024;
const MAX_EXPANDED_BYTES = 12 * 1024 * 1024;
const MAX_ARCHIVE_ENTRIES = 10;
const ALLOWED_ENTRY = /^(system\.json|startup-error\.log|watchdog\.log|session-[0-9]{8}-[0-9]{6}\.log(?:\.jsonl)?)$/;

export function validateZip(bytes) {
  if (bytes.byteLength < 22 || bytes.byteLength > MAX_UPLOAD_BYTES) throw new Error("invalid_size");
  const view = new DataView(bytes);
  const minEnd = Math.max(0, bytes.byteLength - 22 - 65535);
  let end = -1;
  for (let i = bytes.byteLength - 22; i >= minEnd; i--) {
    if (view.getUint32(i, true) === 0x06054b50) { end = i; break; }
  }
  if (end < 0) throw new Error("missing_end_record");
  const disk = view.getUint16(end + 4, true);
  const centralDisk = view.getUint16(end + 6, true);
  const entriesOnDisk = view.getUint16(end + 8, true);
  const entryCount = view.getUint16(end + 10, true);
  const centralSize = view.getUint32(end + 12, true);
  const centralOffset = view.getUint32(end + 16, true);
  const commentLength = view.getUint16(end + 20, true);
  if (disk !== 0 || centralDisk !== 0 || entriesOnDisk !== entryCount || entryCount < 1 || entryCount > MAX_ARCHIVE_ENTRIES ||
      end + 22 + commentLength !== bytes.byteLength || centralOffset + centralSize > end ||
      centralOffset >= end || centralSize > end - centralOffset) throw new Error("invalid_directory");

  const decoder = new TextDecoder("utf-8", { fatal: true });
  const names = new Set();
  const entries = [];
  let offset = centralOffset;
  let expandedBytes = 0;
  for (let index = 0; index < entryCount; index++) {
    if (offset + 46 > end || view.getUint32(offset, true) !== 0x02014b50) throw new Error("invalid_entry");
    const flags = view.getUint16(offset + 8, true);
    const method = view.getUint16(offset + 10, true);
    const checksum = view.getUint32(offset + 16, true);
    const compressedSize = view.getUint32(offset + 20, true);
    const expandedSize = view.getUint32(offset + 24, true);
    const nameLength = view.getUint16(offset + 28, true);
    const extraLength = view.getUint16(offset + 30, true);
    const entryCommentLength = view.getUint16(offset + 32, true);
    const localOffset = view.getUint32(offset + 42, true);
    const recordLength = 46 + nameLength + extraLength + entryCommentLength;
    if (nameLength < 1 || offset + recordLength > end || (flags & 1) !== 0 || (method !== 0 && method !== 8) ||
        compressedSize === 0xffffffff || expandedSize === 0xffffffff || expandedSize > MAX_EXPANDED_BYTES) throw new Error("unsupported_entry");
    const name = decoder.decode(new Uint8Array(bytes, offset + 46, nameLength));
    if (!ALLOWED_ENTRY.test(name) || names.has(name)) throw new Error("invalid_entry_name");
    names.add(name);
    expandedBytes += expandedSize;
    if (expandedBytes > MAX_EXPANDED_BYTES) throw new Error("expanded_size_limit");
    if (localOffset + 30 > centralOffset || view.getUint32(localOffset, true) !== 0x04034b50) throw new Error("invalid_local_header");
    const localFlags = view.getUint16(localOffset + 6, true);
    const localMethod = view.getUint16(localOffset + 8, true);
    const localNameLength = view.getUint16(localOffset + 26, true);
    const localExtraLength = view.getUint16(localOffset + 28, true);
    const dataOffset = localOffset + 30 + localNameLength + localExtraLength;
    if ((localFlags & 1) !== 0 || localMethod !== method || dataOffset + compressedSize > centralOffset) throw new Error("invalid_local_data");
    const localName = decoder.decode(new Uint8Array(bytes, localOffset + 30, localNameLength));
    if (localName !== name) throw new Error("entry_name_mismatch");
    entries.push({ name, method, checksum, compressedSize, expandedSize, dataOffset });
    offset += recordLength;
  }
  if (offset !== centralOffset + centralSize || !names.has("system.json")) throw new Error("invalid_archive_contents");
  return entries;
}

const CRC_TABLE = Uint32Array.from({ length: 256 }, (_, index) => {
  let value = index;
  for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
  return value >>> 0;
});

function crc32(bytes) {
  let crc = -1;
  for (const value of bytes) crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ value) & 255];
  return (crc ^ -1) >>> 0;
}

async function expandEntry(bytes, entry) {
  const compressed = new Uint8Array(bytes, entry.dataOffset, entry.compressedSize);
  if (entry.method === 0) {
    if (compressed.byteLength !== entry.expandedSize || crc32(compressed) !== entry.checksum)
      throw new Error("entry_integrity_failed");
    return compressed;
  }
  const reader = new Blob([compressed]).stream().pipeThrough(new DecompressionStream("deflate-raw")).getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > entry.expandedSize || length > MAX_EXPANDED_BYTES) throw new Error("expanded_size_limit");
      chunks.push(value);
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally { reader.releaseLock(); }
  if (length !== entry.expandedSize) throw new Error("entry_size_mismatch");
  const output = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { output.set(chunk, offset); offset += chunk.byteLength; }
  if (crc32(output) !== entry.checksum) throw new Error("entry_integrity_failed");
  return output;
}

export async function openReport(bytes) {
  const entries = validateZip(bytes);
  const decoder = new TextDecoder("utf-8", { fatal: true });
  const files = [];
  for (const entry of entries) {
    const content = decoder.decode(await expandEntry(bytes, entry));
    files.push({ name: entry.name, size: entry.expandedSize, content });
  }
  files.sort((a, b) => a.name === "system.json" ? -1 : b.name === "system.json" ? 1 : a.name.localeCompare(b.name));
  return files;
}
