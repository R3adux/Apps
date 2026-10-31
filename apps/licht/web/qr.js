// QR codes in the app: drawing one (sharing lamps) and reading one with the camera (adding lamps).
// Both happen in the page itself – nothing is sent anywhere.
import { encode } from 'uqr';
import jsQR from 'jsqr';

const QUIET_ZONE = 4; // white border scanners need

/** The modules of a QR code: rows of booleans (true = dark), without the quiet zone. */
export function qrMatrix(text) {
  // Error correction M: still readable from a slightly blurry photo of a screen.
  return encode(text, { ecc: 'M', border: 0 }).data;
}

/** A QR code as SVG text: black on white, sharp at any size. */
export function qrSvg(text) {
  const rows = qrMatrix(text);
  const size = rows.length;
  const total = size + 2 * QUIET_ZONE;
  let path = '';
  rows.forEach((row, y) => {
    for (let x = 0; x < size;) {
      if (!row[x]) {
        x++;
        continue;
      }
      let end = x;
      while (end < size && row[end]) end++;
      path += `M${x + QUIET_ZONE} ${y + QUIET_ZONE}h${end - x}v1h${x - end}z`;
      x = end;
    }
  });
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${total} ${total}" shape-rendering="crispEdges" aria-hidden="true">`
    + `<rect width="${total}" height="${total}" fill="#fff"/><path d="${path}" fill="#000"/></svg>`;
}

/** Reads a QR code from image pixels (RGBA, as from a canvas). The text, or null. */
export function readQr(rgba, width, height) {
  return jsQR(rgba, width, height, { inversionAttempts: 'attemptBoth' })?.data ?? null;
}

/** A sentence for the user about why the camera did not start. */
export function cameraProblem(err) {
  switch (err?.name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return 'Licht darf die Kamera nicht benutzen. Erlaube es in den Android-Einstellungen '
        + '(Apps → Licht → Berechtigungen → Kamera) und versuche es noch einmal.';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return 'Keine Kamera gefunden. Gib den Code stattdessen ein.';
    case 'NotReadableError':
      return 'Die Kamera wird gerade von einer anderen App benutzt.';
    default:
      return 'Die Kamera lässt sich gerade nicht starten. Gib den Code stattdessen ein.';
  }
}

/**
 * Shows the camera picture in `video` and looks for a QR code a few times a second.
 * onCode(text) once a code is read (the camera is off again by then), onProblem(message) when the camera
 * cannot start. Returns stop() – call it when the scanner closes.
 */
export function startScanner(video, onCode, onProblem) {
  let stream = null;
  let timer = null;
  let stopped = false;
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d', { willReadFrequently: true });

  const stop = () => {
    stopped = true;
    clearTimeout(timer);
    stream?.getTracks().forEach((track) => track.stop());
    stream = null;
    video.srcObject = null;
  };

  const look = () => {
    if (stopped) return;
    if (video.readyState >= 2 && video.videoWidth) {
      // The square in the middle – what the user sees in the viewfinder – at most 800 pixels wide.
      const side = Math.min(video.videoWidth, video.videoHeight);
      const scale = Math.min(1, 800 / side);
      canvas.width = canvas.height = Math.round(side * scale);
      context.drawImage(video, (video.videoWidth - side) / 2, (video.videoHeight - side) / 2, side, side,
        0, 0, canvas.width, canvas.height);
      const text = readQr(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
      if (text) {
        stop();
        onCode(text);
        return;
      }
    }
    timer = setTimeout(look, 150);
  };

  (async () => {
    try {
      if (!navigator.mediaDevices?.getUserMedia) throw Object.assign(new Error('no camera API'), { name: 'NotFoundError' });
      const media = await navigator.mediaDevices.getUserMedia({
        audio: false,
        video: { facingMode: 'environment', width: { ideal: 1280 }, height: { ideal: 1280 } },
      });
      if (stopped) {
        media.getTracks().forEach((track) => track.stop());
        return;
      }
      stream = media;
      video.srcObject = media;
      await video.play();
      look();
    } catch (err) {
      if (!stopped) {
        stop();
        onProblem(cameraProblem(err));
      }
    }
  })();

  return stop;
}
