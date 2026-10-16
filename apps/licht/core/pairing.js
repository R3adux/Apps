import { ManualPairingCodeCodec, QrPairingCodeCodec } from '@matter/main/types';

// Accepts the 11/21-digit code printed on the lamp (with or without dashes/spaces)
// or the text of the QR code ("MT:…"). Returns what commissioning needs.
export function parsePairingCode(input) {
  const text = String(input ?? '').trim();
  if (/^MT:/i.test(text)) {
    const [first] = QrPairingCodeCodec.decode(text.toUpperCase());
    return {
      passcode: first.passcode,
      identifierData: { longDiscriminator: first.discriminator },
    };
  }
  const digits = text.replace(/[\s-]/g, '');
  if (!/^(\d{11}|\d{21})$/.test(digits)) {
    throw new Error('Der Code muss 11 oder 21 Ziffern haben (oder mit „MT:“ beginnen).');
  }
  try {
    const { shortDiscriminator, passcode } = ManualPairingCodeCodec.decode(digits);
    return { passcode, identifierData: { shortDiscriminator } };
  } catch {
    throw new Error('Der Code ist ungültig – bitte Ziffern prüfen.');
  }
}
