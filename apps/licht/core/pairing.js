// Matter setup codes: the code printed on a lamp, and share codes for one or several lamps.
//
// Several lamps travel in one QR code the Matter way: the payloads joined with "*" ("MT:…*…").
// Each payload may carry the lamp's network address as a manufacturer-specific TLV element
// (tag 0x80, allowed by the Matter spec and ignored by other apps). It lets the receiving app reach
// the lamp directly, also where the network search (mDNS) is unreliable.

import {
  ManualPairingCodeCodec, QrCodeTlvDataDefaultFields, QrPairingCodeCodec, TlvObject, TlvOptionalField, TlvString,
} from '@matter/main/types';

const ADDRESS_TAG = 0x80;
const AddressTlv = TlvObject({
  ...QrCodeTlvDataDefaultFields,
  address: TlvOptionalField(ADDRESS_TAG, TlvString.bound({ maxLength: 64 })),
});
const MATTER_PORT = 5540;

const invalid = (message) => Object.assign(new Error(message), { status: 400 });

// Reading QR code texts is done here (Matter spec 5.1.3) instead of with matter.js: its Base38 decoder
// rejects payloads whose byte length is a multiple of 3 – valid, and they occur with extra TLV data.
const BASE38 = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-.';
const CHARS_FOR_BYTES = { 1: 2, 2: 4, 3: 5 };

function base38Decode(text) {
  const bytes = [];
  for (let i = 0; i < text.length;) {
    const chars = Math.min(5, text.length - i);
    const count = Object.entries(CHARS_FOR_BYTES).find(([, c]) => c === chars)?.[0];
    if (!count) throw new Error('length');
    let value = 0;
    for (let j = chars - 1; j >= 0; j--) {
      const digit = BASE38.indexOf(text[i + j]);
      if (digit < 0) throw new Error('character');
      value = value * 38 + digit;
    }
    if (value >= 2 ** (8 * count)) throw new Error('value');
    for (let k = 0; k < count; k++) bytes.push(Math.floor(value / 256 ** k) % 256);
    i += chars;
  }
  return Uint8Array.from(bytes);
}

// The fixed 88 bits at the start of a payload, least significant bit first.
function readPayload(bytes) {
  if (bytes.length < 11) throw new Error('short');
  let position = 0;
  const bits = (count) => {
    let value = 0;
    for (let i = 0; i < count; i++, position++) {
      if (bytes[position >> 3] & (1 << (position & 7))) value += 2 ** i;
    }
    return value;
  };
  const payload = {
    version: bits(3), vendorId: bits(16), productId: bits(16), flowType: bits(2), discoveryCapabilities: bits(8),
    discriminator: bits(12), passcode: bits(27),
  };
  if (payload.version !== 0 || payload.passcode < 1 || payload.passcode > 99999998) throw new Error('payload');
  return { ...payload, tlvData: bytes.length > 11 ? bytes.slice(11) : undefined };
}

function decodeQr(text) {
  return text.slice(3).split('*').map((part) => readPayload(base38Decode(part)));
}

/** "192.168.1.20", "192.168.1.20:5540" or "[fe80::1]:5540" → { ip, port, type: 'udp' }, otherwise undefined. */
export function parseAddress(text) {
  const value = String(text ?? '').trim();
  let match = value.match(/^(\d{1,3}(?:\.\d{1,3}){3})(?::(\d{1,5}))?$/);
  if (!match) match = value.match(/^\[([0-9a-fA-F:.]+)\](?::(\d{1,5}))?$/);
  if (!match) return undefined;
  const port = Number(match[2] ?? MATTER_PORT);
  if (port < 1 || port > 65535) return undefined;
  return { ip: match[1], port, type: 'udp' };
}

/** { ip, port } → "192.168.1.20:5540" / "[fe80::1]:5540" */
export function formatAddress({ ip, port }) {
  return `${ip.includes(':') ? `[${ip}]` : ip}:${port}`;
}

/**
 * Reads a setup code: the 11/21 digits printed on a lamp (dashes and spaces allowed) or a QR code
 * text "MT:…", which may contain several lamps. Returns one entry per lamp:
 * { passcode, identifierData, address? } – address only if the code carries one.
 */
export function parsePairingCodes(input) {
  const text = String(input ?? '').trim();
  if (/^MT:/i.test(text)) {
    let payloads;
    try {
      payloads = decodeQr(text.toUpperCase());
    } catch {
      throw invalid('Der QR-Code ist ungültig oder unvollständig.');
    }
    return payloads.map((payload) => ({
      passcode: payload.passcode,
      identifierData: { longDiscriminator: payload.discriminator },
      address: addressIn(payload),
    }));
  }
  const digits = text.replace(/[\s-]/g, '');
  if (!/^(\d{11}|\d{21})$/.test(digits)) {
    throw invalid('Der Code muss 11 oder 21 Ziffern haben (oder ein QR-Code-Text sein, der mit „MT:“ beginnt).');
  }
  try {
    const { shortDiscriminator, passcode } = ManualPairingCodeCodec.decode(digits);
    return [{ passcode, identifierData: { shortDiscriminator } }];
  } catch {
    throw invalid('Der Code ist ungültig – bitte Ziffern prüfen.');
  }
}

/** The first (usually only) lamp of a setup code. */
export const parsePairingCode = (input) => parsePairingCodes(input)[0];

function addressIn(payload) {
  if (!payload.tlvData?.byteLength) return undefined;
  try {
    return parseAddress(QrPairingCodeCodec.decodeTlvData(payload.tlvData, AddressTlv).address);
  } catch {
    return undefined; // other data from another app: not ours
  }
}

/**
 * One share code for several lamps. entries: [{ qr: 'MT:…' (one lamp), address?: { ip, port } }].
 * Returns the combined QR code text "MT:…*…".
 */
export function shareCode(entries) {
  if (!entries.length) throw new Error('Keine Lampe zum Teilen.');
  const payloads = entries.map(({ qr, address }) => {
    const [payload] = decodeQr(qr);
    if (!address || payload.tlvData?.byteLength) return payload;
    return { ...payload, tlvData: QrPairingCodeCodec.encodeTlvData({ address: formatAddress(address) }, AddressTlv) };
  });
  return QrPairingCodeCodec.encode(payloads);
}
