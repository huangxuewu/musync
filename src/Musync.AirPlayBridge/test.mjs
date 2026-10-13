import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { encodeAlacPacket, AlacDecoder, parseAlacFmtp, readCapabilities } from '@sonn-audio/node-airplay';
import { createCommandDispatcher, createSessionManager } from './receiver-sessions.mjs';

const nextTurn = () => new Promise(resolve => setImmediate(resolve));

test('a waiting receiver command cannot block another receiver close or ping', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  const actions = [], replies = [];
  const dispatcher = createCommandDispatcher({
    async setVolume(id) { actions.push('volume:' + id); entered.resolve(); await held.promise; },
    sendPcm(id, pcm) { assert.equal(pcm.length, 1408); actions.push('pcm:' + id); },
    async close(id) { actions.push('close:' + id); },
    async shutdown() { actions.push('shutdown'); }
  }, (request, result) => replies.push({ request, ...result }));
  const waiting = dispatcher.dispatch({ op: 'volume', id: 'slow', request: 'held', volume: 50 });
  await entered.promise;
  const close = dispatcher.dispatch({ op: 'close', id: 'healthy', request: 'healthy-close' });
  const ping = dispatcher.dispatch({ op: 'ping', request: 'ping' });
  await dispatcher.dispatch({ op: 'pcm', id: 'slow', data: Buffer.alloc(1408).toString('base64') });
  try {
    await nextTurn();
    assert.ok(actions.includes('close:healthy'), 'The healthy close was blocked by another receiver');
    assert.ok(replies.some(reply => reply.request === 'healthy-close' && reply.ok));
    assert.ok(replies.some(reply => reply.request === 'ping' && reply.version === '0.1.0'));
    assert.ok(actions.includes('pcm:slow'));
    assert.ok(!replies.some(reply => reply.request === 'held'));
  } finally { held.resolve(); await Promise.all([waiting, close, ping]); await dispatcher.shutdown(); }
});

test('receiver commands keep their order and a rejected control does not block close', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  const actions = [], replies = [];
  const dispatcher = createCommandDispatcher({
    async open() { actions.push('open'); entered.resolve(); await held.promise; },
    async setVolume() { actions.push('volume'); throw new Error('Volume rejected'); },
    async close() { actions.push('close'); },
    async shutdown() { actions.push('shutdown'); }
  }, (request, result) => replies.push({ request, ...result }));
  const open = dispatcher.dispatch({ op: 'open', id: 'one', request: 'open' });
  await entered.promise;
  const volume = dispatcher.dispatch({ op: 'volume', id: 'one', request: 'volume' });
  const close = dispatcher.dispatch({ op: 'close', id: 'one', request: 'close' });
  try { await nextTurn(); assert.deepEqual(actions, ['open']); }
  finally { held.resolve(); await Promise.all([open, volume, close]); await dispatcher.shutdown(); }
  assert.deepEqual(actions, ['open', 'volume', 'close', 'shutdown']);
  assert.deepEqual(replies, [
    { request: 'open', ok: true },
    { request: 'volume', id: 'one', error: 'Volume rejected' },
    { request: 'close', ok: true }
  ]);
});

test('shutdown drains every receiver queue once and accepts no new session', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  const actions = [], replies = [];
  const dispatcher = createCommandDispatcher({
    async open(command) { actions.push('open:' + command.id); entered.resolve(); await held.promise; },
    async close(id) { actions.push('close:' + id); },
    async shutdown() { actions.push('shutdown'); }
  }, (request, result) => replies.push({ request, ...result }));
  const open = dispatcher.dispatch({ op: 'open', id: 'slow', request: 'open' });
  await entered.promise;
  const close = dispatcher.dispatch({ op: 'close', id: 'healthy', request: 'close' });
  const stop = dispatcher.shutdown();
  assert.equal(dispatcher.shutdown(), stop);
  await dispatcher.dispatch({ op: 'open', id: 'late', request: 'late' });
  try {
    await nextTurn();
    assert.deepEqual(actions, ['open:slow', 'close:healthy']);
    assert.ok(replies.some(reply => reply.request === 'late' && /shutting down/.test(reply.error)));
  } finally { held.resolve(); await Promise.all([open, close, stop]); }
  assert.equal(actions.filter(action => action === 'shutdown').length, 1);
});

function simulatedProtocol(options = {}) {
  const state = { actions: [], connections: [], senders: [], sockets: [], clocks: [], ntps: [], errors: [] };
  const protocol = {
    readCapabilities: properties => ({ requiresUserPairing: properties.pin, isAppleReceiver: !properties.ntp, supportsPtp: !properties.ntp }),
    AirPlayConnection: {
      async open(command) {
        const connection = {
          host: command.host, identity: { clockId: 1n }, hap: { sharedSecret: Buffer.alloc(32) }, sessionUrl: 'rtsp://test/session',
          rtsp: { host: command.host, async request(method, request) {
            state.actions.push(method); assert.equal(request.timeoutMs, 2000);
            if (options.failTeardown) throw new Error('Receiver lost');
            return { status: 200 };
          } },
          async setupSession() { state.actions.push('setup'); },
          close() { state.actions.push('connection-close'); command.onEvent('control closed'); },
          drop() { command.onEvent('control dropped'); }
        };
        state.connections.push(connection); return connection;
      }
    },
    PtpEngine: class {
      constructor() { this.peers = new Set(); this.stopped = 0; state.clocks.push(this); }
      async start() { state.actions.push('clock-start'); await options.clockStart?.(); }
      addPeer(host) { this.peers.add(host); }
      removePeer(host) { this.peers.delete(host); }
      stop() { this.stopped++; state.actions.push('clock-stop'); }
    },
    NtpTimingResponder: class {
      constructor() { this.stopped = 0; state.ntps.push(this); }
      async start() { return 1234; }
      stop() { this.stopped++; }
    },
    RealtimeSender: class {
      static async bindSockets() {
        const socket = () => { const result = { closed: 0, close() { this.closed++; } }; state.sockets.push(result); return result; };
        return { data: socket(), control: socket(), dataPort: 1234, controlPort: 1235 };
      }
      constructor(config, data, control) { this.data = data; this.control = control; this.packets = []; this.rtpPosition = 0; state.senders.push(this); }
      start() { state.actions.push('sender-start'); }
      stop() { state.actions.push('sender-stop'); this.data.close(); this.control.close(); }
      sendPacket(pcm) { this.packets.push(pcm); }
    },
    async setupRealtimeStream(rtsp) { await options.stream?.(rtsp); if (options.failStream) throw new Error('Stream rejected'); return { dataPort: 1234, controlPort: 1235 }; },
    async setVolume(rtsp, uri, value) { state.actions.push('volume:' + value); },
    async setMetadata(rtsp) { state.actions.push('metadata'); await options.metadata?.(rtsp); if (options.failMetadata) throw new Error('Metadata rejected'); }
  };
  const manager = createSessionManager(protocol, Buffer.alloc(8), (id, error) => state.errors.push({ id, error }));
  return { manager, state };
}
const simulatedSpeaker = (id = 'one', properties = {}) => ({ id, host: id, port: 7000, volume: 42, properties });

test('a pending handshake allows another receiver to open and close without losing their shared clock', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  const replies = [];
  const { manager, state } = simulatedProtocol({ stream: async rtsp => {
    if (rtsp.host === 'one') { entered.resolve(); await held.promise; }
  } });
  const dispatcher = createCommandDispatcher(manager, (request, result) => replies.push({ request, ...result }));
  const first = dispatcher.dispatch({ ...simulatedSpeaker('one'), op: 'open', request: 'slow-open' });
  await entered.promise;
  const second = dispatcher.dispatch({ ...simulatedSpeaker('two'), op: 'open', request: 'healthy-open' });
  try {
    await nextTurn();
    assert.ok(replies.some(reply => reply.request === 'healthy-open' && reply.ok));
    assert.ok(!replies.some(reply => reply.request === 'slow-open'));
    await dispatcher.dispatch({ op: 'close', id: 'two', request: 'healthy-close' });
    assert.equal(state.clocks.length, 1); assert.equal(state.clocks[0].stopped, 0);
    assert.deepEqual([...state.clocks[0].peers], ['one']);
  } finally { held.resolve(); await Promise.all([first, second]); await dispatcher.shutdown(); }
  assert.equal(state.clocks[0].stopped, 1);
  assert.ok(state.sockets.every(socket => socket.closed === 1));
});

test('concurrent receivers wait for the same shared clock to finish starting', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  const { manager, state } = simulatedProtocol({ clockStart: async () => { entered.resolve(); await held.promise; } });
  const first = manager.open(simulatedSpeaker('one'));
  await entered.promise;
  const second = manager.open(simulatedSpeaker('two'));
  try {
    await nextTurn();
    assert.equal(state.clocks.length, 1);
    assert.ok(!state.actions.includes('setup'), 'A receiver used the clock before it finished starting');
  } finally { held.resolve(); await Promise.all([first, second]); await manager.shutdown(); }
});

test('a failed receiver setup cannot stop the clock owned by another pending setup', { timeout: 5000 }, async () => {
  const fail = Promise.withResolvers(), firstEntered = Promise.withResolvers();
  const ready = Promise.withResolvers(), secondEntered = Promise.withResolvers();
  const { manager, state } = simulatedProtocol({
    stream: async rtsp => { if (rtsp.host === 'one') { firstEntered.resolve(); await fail.promise; throw new Error('First setup failed'); } },
    metadata: async rtsp => { if (rtsp.host === 'two') { secondEntered.resolve(); await ready.promise; } }
  });
  const first = manager.open(simulatedSpeaker('one'));
  const failed = assert.rejects(first, /First setup failed/);
  await firstEntered.promise;
  const second = manager.open(simulatedSpeaker('two'));
  await secondEntered.promise;
  try {
    fail.resolve(); await failed;
    assert.equal(state.clocks[0].stopped, 0, 'The failed receiver stopped a clock still owned by the pending receiver');
    assert.deepEqual([...state.clocks[0].peers], ['two']);
  } finally { fail.resolve(); ready.resolve(); await failed; await second; await manager.shutdown(); }
  assert.equal(state.clocks[0].stopped, 1);
});

test('receivers sharing a host keep its clock peer until both sessions close', { timeout: 5000 }, async () => {
  const { manager, state } = simulatedProtocol();
  const one = simulatedSpeaker('one'), two = { ...simulatedSpeaker('two'), host: 'one' };
  await Promise.all([manager.open(one), manager.open(two)]);
  assert.equal(state.clocks.length, 1); assert.deepEqual([...state.clocks[0].peers], ['one']);
  await manager.close('one');
  assert.equal(state.clocks[0].stopped, 0); assert.deepEqual([...state.clocks[0].peers], ['one']);
  manager.sendPcm('two', Buffer.alloc(1408)); assert.equal(state.senders[1].packets.length, 1);
  await manager.close('two');
  assert.equal(state.clocks[0].stopped, 1); assert.equal(state.clocks[0].peers.size, 0);
});

test('shared clock startup failure releases all pending users and the next attempt starts fresh', { timeout: 5000 }, async () => {
  const held = Promise.withResolvers(), entered = Promise.withResolvers();
  let starts = 0;
  const { manager, state } = simulatedProtocol({ clockStart: async () => {
    if (++starts === 1) { entered.resolve(); await held.promise; throw new Error('Clock startup failed'); }
  } });
  const first = manager.open(simulatedSpeaker('one'));
  const firstFailure = assert.rejects(first, /Clock startup failed/);
  await entered.promise;
  const second = manager.open(simulatedSpeaker('two'));
  const secondFailure = assert.rejects(second, /Clock startup failed/);
  await nextTurn(); held.resolve(); await Promise.all([firstFailure, secondFailure]);
  assert.equal(starts, 1); assert.equal(state.clocks[0].stopped, 1);
  assert.ok(!state.actions.includes('setup')); assert.equal(state.senders.length, 0);
  assert.equal(state.actions.filter(action => action === 'connection-close').length, 2);
  await manager.open(simulatedSpeaker('one'));
  assert.equal(starts, 2); assert.equal(state.clocks.length, 2);
  await manager.shutdown(); assert.equal(state.clocks[1].stopped, 1);
});

test('AirPlay setup applies controls before starting and close tears down without a false disconnect', async () => {
  const { manager, state } = simulatedProtocol();
  await manager.open(simulatedSpeaker()); manager.sendPcm('one', Buffer.alloc(1408));
  await manager.close('one'); manager.sendPcm('one', Buffer.alloc(1408));
  assert.equal(state.senders[0].packets.length, 1); assert.deepEqual(state.errors, []);
  assert.ok(state.actions.indexOf('volume:42') < state.actions.indexOf('sender-start'));
  assert.ok(state.actions.indexOf('metadata') < state.actions.indexOf('sender-start'));
  assert.ok(state.actions.indexOf('sender-stop') < state.actions.indexOf('TEARDOWN'));
  assert.ok(state.actions.indexOf('TEARDOWN') < state.actions.indexOf('connection-close'));
  assert.equal(state.clocks[0].stopped, 1); assert.ok(state.sockets.every(s => s.closed === 1));
});
test('AirPlay shares its clock until the last receiver closes and recreates it on resume', async () => {
  const { manager, state } = simulatedProtocol();
  await manager.open(simulatedSpeaker('one')); await manager.open(simulatedSpeaker('two'));
  assert.equal(state.clocks.length, 1);
  await manager.close('one'); assert.equal(state.clocks[0].stopped, 0);
  assert.deepEqual([...state.clocks[0].peers], ['two']);
  await manager.close('two'); assert.equal(state.clocks[0].stopped, 1);
  await manager.open(simulatedSpeaker('one')); assert.equal(state.clocks.length, 2);
  await manager.shutdown(); assert.equal(state.clocks[1].stopped, 1);
});
test('AirPlay reports unexpected active connection loss', async () => {
  const { manager, state } = simulatedProtocol(); await manager.open(simulatedSpeaker());
  state.connections[0].drop(); assert.deepEqual(state.errors, [{ id: 'one', error: 'control dropped' }]);
  await manager.shutdown(); assert.equal(state.errors.length, 1);
});
test('AirPlay metadata failure releases the sender without starting it', async () => {
  const { manager, state } = simulatedProtocol({ failMetadata: true });
  await assert.rejects(manager.open(simulatedSpeaker()), /Metadata rejected/);
  assert.ok(!state.actions.includes('sender-start')); assert.ok(state.actions.includes('sender-stop'));
  assert.ok(state.sockets.every(s => s.closed === 1)); assert.equal(state.clocks[0].stopped, 1);
  assert.deepEqual(state.errors, []);
});
test('AirPlay stream setup failure releases allocated sockets and NTP timing', async () => {
  const { manager, state } = simulatedProtocol({ failStream: true });
  await assert.rejects(manager.open(simulatedSpeaker('one', { ntp: true })), /Stream rejected/);
  assert.equal(state.senders.length, 0); assert.ok(state.sockets.every(s => s.closed === 1));
  assert.equal(state.ntps[0].stopped, 1); assert.equal(state.clocks[0].stopped, 1);
});
test('AirPlay teardown failure still releases resources and close remains idempotent', async () => {
  const { manager, state } = simulatedProtocol({ failTeardown: true }); await manager.open(simulatedSpeaker());
  await manager.close('one'); await manager.close('one');
  assert.ok(state.sockets.every(s => s.closed === 1)); assert.equal(state.clocks[0].stopped, 1);
  assert.deepEqual(state.errors, []); await assert.rejects(manager.setVolume('one', 50), /not open/);
});
test('AirPlay pairing restrictions are checked before transport resources are created', async () => {
  const { manager, state } = simulatedProtocol();
  await assert.rejects(manager.open(simulatedSpeaker('one', { pin: true })), /PIN\/password/);
  assert.equal(state.connections.length, 0); assert.equal(state.clocks.length, 0);
});

test('ALAC wire packet decodes to identical PCM including signed extremes', () => {
  const pcm = Buffer.alloc(1408);
  for (let sample = 0; sample < 704; sample++) pcm.writeInt16LE(sample % 2 ? -32768 + sample : 32767 - sample, sample * 2);
  const packet = encodeAlacPacket(pcm, {framesPerPacket:352, bitDepth:16, channels:2});
  const config = parseAlacFmtp('352 0 16 40 10 14 2 255 0 0 44100');
  assert.deepEqual(new AlacDecoder(config).decode(packet), pcm);
});
test('HomePod uses an authenticated modern lane with PTP capability', () => {
  const capability = readCapabilities({ model:'AudioAccessory5,1', features:'0x0,0x20240', flags:'0' });
  assert.equal(capability.isAppleReceiver, true);
  assert.equal(capability.supportsPtp, true);
  assert.equal(capability.supportsAirPlay2, true);
  assert.equal(capability.requiresUserPairing, false);
});
test('bridge starts and exits cleanly without binding receiver or timing ports', async () => {
  const child = spawn(process.execPath, ['bridge.mjs'], {cwd:import.meta.dirname, stdio:['pipe','pipe','pipe']});
  const answer = new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {child.kill(); reject(new Error('Bridge ping timed out'));}, 3000);
    child.stdout.once('data', bytes => {clearTimeout(timeout); resolve(JSON.parse(bytes.toString()));});
    child.once('error', reject);
  });
  child.stdin.write(JSON.stringify({op:'ping',request:'probe'}) + '\n');
  assert.equal((await answer).version, '0.1.0');
  child.stdin.end();
  await new Promise(resolve => child.once('exit', resolve));
});
