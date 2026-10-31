// Stands in for LichtFenster.exe in tests: speaks its protocol (pc/bridge.js) over stdin/stdout and writes
// everything it receives to FAKE_WINDOW_LOG (one JSON object per line), so a test can follow along.
// Commands for the test come as files: it asks for the lamps, and quits when FAKE_WINDOW_LOG.quit exists.
import { appendFileSync, existsSync } from 'node:fs';
import { createInterface } from 'node:readline';

const logFile = process.env.FAKE_WINDOW_LOG;
const record = (entry) => logFile && appendFileSync(logFile, `${JSON.stringify(entry)}\n`);
const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`);

record({ t: 'started', args: process.argv.slice(2) });
if (process.env.FAKE_WINDOW_CRASH === '1') process.exit(3);
send({ t: 'ready' });
let asked = false;
createInterface({ input: process.stdin }).on('line', (line) => {
  const message = JSON.parse(line);
  record(message);
  if (message.t === 'lights' && !asked) {
    asked = true;
    send({ t: 'call', id: 1, method: 'lights', args: [] });
    send({ t: 'call', id: 2, method: 'autostart', args: [] });
    send({ t: 'call', id: 3, method: 'update', args: ['99', { on: true }] }); // no such lamp → error reply
  }
  if (message.t === 'quit') process.exit(0);
});
process.stdin.on('end', () => process.exit(0));
setInterval(() => {
  if (logFile && existsSync(`${logFile}.quit`)) send({ t: 'quit' });
}, 100);
