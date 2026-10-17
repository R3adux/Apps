// Stands in for LichtFenster.exe in tests: speaks the same line protocol (see desktop/Program.cs).
import { createInterface } from 'node:readline';

const args = process.argv.slice(2);
const send = (line) => process.stdout.write(`${line}\n`);
send(`log:args ${args.join(' ')}`);
send('ready');
if (args.includes('--show')) send('window:loaded 5 fake');
if (process.env.FAKE_DESKTOP_CRASH === '1') process.exit(3);
if (process.env.FAKE_DESKTOP_MENU === '1') ['open-browser', 'autostart:off', 'quit'].forEach(send); // menu clicks

createInterface({ input: process.stdin }).on('line', (line) => {
  if (line === 'show') send('window:loaded 1 fake');
  else if (line.startsWith('autostart:')) send(`log:${line}`);
  else if (line === 'quit') process.exit(0);
});
process.stdin.on('end', () => process.exit(0));
