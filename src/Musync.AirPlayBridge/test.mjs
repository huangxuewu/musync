import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { encodeAlacPacket, AlacDecoder, parseAlacFmtp, readCapabilities } from '@sonn-audio/node-airplay';
import { createSessionManager } from './receiver-sessions.mjs';

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
      async start() { state.actions.push('clock-start'); }
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
    async setupRealtimeStream() { if (options.failStream) throw new Error('Stream rejected'); return { dataPort: 1234, controlPort: 1235 }; },
    async setVolume(rtsp, uri, value) { state.actions.push('volume:' + value); },
    async setMetadata() { state.actions.push('metadata'); if (options.failMetadata) throw new Error('Metadata rejected'); }
  };
  const manager = createSessionManager(protocol, Buffer.alloc(8), (id, error) => state.errors.push({ id, error }));
  return { manager, state };
}
const simulatedSpeaker = (id = 'one', properties = {}) => ({ id, host: id, port: 7000, volume: 42, properties });

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
