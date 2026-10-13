import { createInterface } from 'node:readline';
import { createHash } from 'node:crypto';
import * as protocol from '@sonn-audio/node-airplay';
import { createSessionManager } from './receiver-sessions.mjs';

// One process serves every receiver: PTP's fixed ports cannot be shared by
// independent senders. Only newline-delimited commands on stdin are accepted.
const identity = createHash('sha256').update(process.env.COMPUTERNAME ?? 'Musync').digest().subarray(0, 8);
const reply = (request, value = {}) => process.stdout.write(JSON.stringify({ request, ...value }) + '\n');
const manager = createSessionManager(protocol, identity, (id, error) => reply(null, { id, error }));
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
let chain = Promise.resolve();
input.on('line', line => {
  if (line.length > 16384) return;
  let command;
  try { command = JSON.parse(line); } catch { return; }
  if (command.op === 'pcm') {
    try { manager.sendPcm(command.id, Buffer.from(command.data, 'base64')); }
    catch (error) { reply(null, { id: command.id, error: error.message }); }
    return;
  }
  chain = chain.then(async () => {
    try {
      if (command.op === 'open') await manager.open(command);
      else if (command.op === 'close') await manager.close(command.id);
      else if (command.op === 'volume') await manager.setVolume(command.id, command.volume);
      else if (command.op === 'ping') { reply(command.request, { version: '0.1.0' }); return; }
      else throw new Error('Unknown operation');
      reply(command.request, { ok: true });
    } catch (error) { reply(command.request, { id: command.id, error: error.message }); }
  });
});
input.on('close', async () => {
  await chain;
  await manager.shutdown();
  process.exit(0);
});
process.on('uncaughtException', async error => {
  reply(null, { error: error.message });
  try { await manager.shutdown(); } finally { process.exit(1); }
});
