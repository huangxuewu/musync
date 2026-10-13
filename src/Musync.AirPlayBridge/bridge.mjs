import { createInterface } from 'node:readline';
import { createHash } from 'node:crypto';
import * as protocol from '@sonn-audio/node-airplay';
import { createCommandDispatcher, createSessionManager } from './receiver-sessions.mjs';

// One process serves every receiver: PTP's fixed ports cannot be shared by
// independent senders. Only newline-delimited commands on stdin are accepted.
const identity = createHash('sha256').update(process.env.COMPUTERNAME ?? 'Musync').digest().subarray(0, 8);
const reply = (request, value = {}) => process.stdout.write(JSON.stringify({ request, ...value }) + '\n');
const manager = createSessionManager(protocol, identity, (id, error) => reply(null, { id, error }));
const dispatcher = createCommandDispatcher(manager, reply);
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', line => {
  if (line.length > 16384) return;
  let command;
  try { command = JSON.parse(line); } catch { return; }
  dispatcher.dispatch(command);
});
input.on('close', async () => {
  await dispatcher.shutdown();
  process.exit(0);
});
process.on('uncaughtException', async error => {
  reply(null, { error: error.message });
  try { await manager.shutdown(); } finally { process.exit(1); }
});
