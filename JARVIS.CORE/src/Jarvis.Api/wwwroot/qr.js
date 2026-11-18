// Minimal QR Code encoder for owner pairing links: byte mode, error correction level L, versions 1-10.
// No third-party package: the structure follows ISO/IEC 18004 and is checked against a reference encoder in tests.
const BLOCKS = [null, [7, [19]], [10, [34]], [15, [55]], [20, [80]], [26, [108]], [18, [68, 68]], [20, [78, 78]],
  [24, [97, 97]], [30, [116, 116]], [18, [68, 68, 69, 69]]];
const ALIGN = [null, [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]];
const EXP = new Uint8Array(512), LOG = new Uint8Array(256);
for (let i = 0, x = 1; i < 255; i++) { EXP[i] = x; LOG[x] = i; x = (x << 1) ^ (x & 0x80 ? 0x11d : 0); }
for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
const mul = (a, b) => a && b ? EXP[LOG[a] + LOG[b]] : 0;

function generator(degree) {
  let poly = [1];
  for (let i = 0; i < degree; i++) {
    const next = new Array(poly.length + 1).fill(0);
    for (let j = 0; j < poly.length; j++) { next[j] ^= poly[j]; next[j + 1] ^= mul(poly[j], EXP[i]); }
    poly = next;
  }
  return poly;
}
function remainder(data, degree) {
  const gen = generator(degree), result = new Array(degree).fill(0);
  for (const byte of data) {
    const factor = byte ^ result.shift(); result.push(0);
    for (let i = 0; i < degree; i++) result[i] ^= mul(gen[i + 1], factor);
  }
  return result;
}

export function qrMatrix(text, mask = 0) {
  const bytes = [...new TextEncoder().encode(text)];
  let version = 1;
  for (; version <= 10; version++) {
    const capacity = BLOCKS[version][1].reduce((a, b) => a + b, 0) * 8;
    if (4 + (version < 10 ? 8 : 16) + bytes.length * 8 <= capacity) break;
  }
  if (version > 10) throw new Error('Pairing link is too long for a QR code.');
  const [ecLength, blockLengths] = BLOCKS[version], capacity = blockLengths.reduce((a, b) => a + b, 0);
  const bits = [];
  const put = (value, length) => { for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1); };
  put(4, 4); put(bytes.length, version < 10 ? 8 : 16); for (const b of bytes) put(b, 8);
  put(0, Math.min(4, capacity * 8 - bits.length)); while (bits.length % 8) bits.push(0);
  const data = []; for (let i = 0; i < bits.length; i += 8) data.push(parseInt(bits.slice(i, i + 8).join(''), 2));
  for (let pad = 0xec; data.length < capacity; pad ^= 0xec ^ 0x11) data.push(pad);

  const blocks = [], ecc = []; let offset = 0;
  for (const length of blockLengths) { const block = data.slice(offset, offset += length); blocks.push(block); ecc.push(remainder(block, ecLength)); }
  const codewords = [];
  for (let i = 0; i < Math.max(...blockLengths); i++) for (const block of blocks) if (i < block.length) codewords.push(block[i]);
  for (let i = 0; i < ecLength; i++) for (const block of ecc) codewords.push(block[i]);

  const size = 17 + version * 4;
  const modules = Array.from({ length: size }, () => new Array(size).fill(false));
  const fixed = Array.from({ length: size }, () => new Array(size).fill(false));
  const set = (x, y, dark) => { modules[y][x] = dark; fixed[y][x] = true; };
  for (let i = 0; i < size; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]])
    for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
      const x = cx + dx, y = cy + dy, d = Math.max(Math.abs(dx), Math.abs(dy));
      if (x >= 0 && x < size && y >= 0 && y < size) set(x, y, d !== 2 && d !== 4);
    }
  const align = ALIGN[version], last = align.length - 1;
  for (let i = 0; i < align.length; i++) for (let j = 0; j < align.length; j++) {
    if (i === 0 && j === 0 || i === 0 && j === last || i === last && j === 0) continue;
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(align[i] + dx, align[j] + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
  }
  const format = () => {
    const value = (1 << 3) | mask; let rem = value;
    for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
    const f = ((value << 10) | rem) ^ 0x5412, bit = i => ((f >>> i) & 1) === 1;
    for (let i = 0; i <= 5; i++) set(8, i, bit(i));
    set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
    for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
    for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
    for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
    set(8, size - 8, true);
  };
  format();
  if (version >= 7) {
    let rem = version; for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
    const v = (version << 12) | rem;
    for (let i = 0; i < 18; i++) { const dark = ((v >>> i) & 1) === 1, a = size - 11 + (i % 3), b = Math.floor(i / 3); set(a, b, dark); set(b, a, dark); }
  }
  let index = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vertical = 0; vertical < size; vertical++) for (let j = 0; j < 2; j++) {
      const x = right - j, upward = ((right + 1) & 2) === 0, y = upward ? size - 1 - vertical : vertical;
      if (fixed[y][x]) continue;
      let dark = index < codewords.length * 8 && ((codewords[index >>> 3] >>> (7 - (index & 7))) & 1) === 1; index++;
      const masks = [(x + y) % 2, y % 2, x % 3, (x + y) % 3, (Math.floor(x / 3) + Math.floor(y / 2)) % 2,
        x * y % 2 + x * y % 3, (x * y % 2 + x * y % 3) % 2, ((x + y) % 2 + x * y % 3) % 2];
      if (masks[mask] === 0) dark = !dark;
      modules[y][x] = dark;
    }
  }
  return { size, version, modules };
}

// Dark modules on white with the standard four-module quiet zone, so phone cameras read it in either theme.
export function qrSvg(text, pixels = 240) {
  const { size, modules } = qrMatrix(text), total = size + 8;
  let path = '';
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (modules[y][x]) path += `M${x + 4} ${y + 4}h1v1h-1z`;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${total} ${total}`); svg.setAttribute('width', pixels); svg.setAttribute('height', pixels);
  svg.setAttribute('role', 'img'); svg.setAttribute('aria-label', 'Pairing QR code. Scan it with your phone camera.');
  svg.setAttribute('shape-rendering', 'crispEdges');
  const background = document.createElementNS(svg.namespaceURI, 'rect');
  background.setAttribute('width', total); background.setAttribute('height', total); background.setAttribute('fill', '#ffffff');
  const dark = document.createElementNS(svg.namespaceURI, 'path'); dark.setAttribute('d', path); dark.setAttribute('fill', '#000000');
  svg.append(background, dark);
  return svg;
}
