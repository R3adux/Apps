// Settings of Licht on the PC, from the environment (mainly for tests and troubleshooting).
//   LICHT_DATA     data folder (default: <user folder>/.licht) – lamp data, licht.log, window
//   LICHT_LOG      matter.js log level, e.g. info or debug (default: error)
//   LICHT_WINDOW   window program to use instead of the built-in one (an .exe, or a .js/.mjs file run
//                  with Node – used by the tests)
import { homedir } from 'node:os';
import { join } from 'node:path';

export const dataDirectory = () => process.env.LICHT_DATA ?? join(homedir(), '.licht');
export const logLevel = () => process.env.LICHT_LOG ?? 'error';
