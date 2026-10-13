// The command dispatcher is separate from transport setup so tests can hold
// a receiver's reply without connecting to speakers or binding timing ports.
export function createCommandDispatcher(manager, reply) {
  const queues = new Map();
  let accepting = true;
  let shutdown;
  function dispatch(command) {
    if (!accepting) { reply(command.request, { id: command.id, error: 'AirPlay bridge is shutting down' }); return Promise.resolve(); }
    if (command.op === 'pcm') {
      try { manager.sendPcm(command.id, Buffer.from(command.data, 'base64')); }
      catch (error) { reply(null, { id: command.id, error: error.message }); }
      return Promise.resolve();
    }
    if (command.op === 'ping') { reply(command.request, { version: '0.1.0' }); return Promise.resolve(); }
    if (typeof command.id !== 'string' || !command.id) {
      reply(command.request, { error: 'Invalid receiver ID' }); return Promise.resolve();
    }
    // A slow handshake or control reply must not hold up other receivers.
    // Commands for one receiver still retain their original order.
    const pending = (queues.get(command.id) ?? Promise.resolve()).then(async () => {
      try {
        if (command.op === 'open') await manager.open(command);
        else if (command.op === 'close') await manager.close(command.id);
        else if (command.op === 'volume') await manager.setVolume(command.id, command.volume);
        else throw new Error('Unknown operation');
        reply(command.request, { ok: true });
      } catch (error) { reply(command.request, { id: command.id, error: error.message }); }
    });
    queues.set(command.id, pending);
    const clear = () => { if (queues.get(command.id) === pending) queues.delete(command.id); };
    pending.then(clear, clear);
    return pending;
  }
  function stop() {
    accepting = false;
    return shutdown ??= (async () => { await Promise.allSettled([...queues.values()]); await manager.shutdown(); })();
  }
  return { dispatch, shutdown: stop };
}

// Inject transport resources so lifecycle tests do not bind real ports.
export function createSessionManager(protocol, identity, reportError) {
  const sessions = new Map();
  let clock;
  async function acquireClock(session) {
    let shared = clock;
    if (!shared) {
      shared = {
        engine: new protocol.PtpEngine({ clockId: session.connection.identity.clockId, peers: [] }),
        users: new Set(), peers: new Map()
      };
      clock = shared;
      shared.ready = Promise.resolve().then(() => shared.engine.start());
    }
    // Pending handshakes own the clock too. Every receiver waits for the one
    // startup attempt before setting up its stream or using the timing ports.
    shared.users.add(session); session.clock = shared;
    await shared.ready;
    const peers = shared.peers.get(session.host) ?? 0;
    if (peers === 0) shared.engine.addPeer(session.host);
    shared.peers.set(session.host, peers + 1); session.clockPeer = true;
    return shared.engine;
  }
  function releaseClock(session) {
    const shared = session.clock;
    if (!shared) return;
    session.clock = undefined;
    if (session.clockPeer) {
      const peers = shared.peers.get(session.host) - 1;
      if (peers === 0) { shared.peers.delete(session.host); shared.engine.removePeer(session.host); }
      else shared.peers.set(session.host, peers);
    }
    shared.users.delete(session);
    if (shared.users.size === 0) {
      try { shared.engine.stop(); }
      finally { if (clock === shared) clock = undefined; }
    }
  }
  function volume(value) {
    if (!Number.isFinite(value)) throw new Error('Invalid volume');
    return Math.max(0, Math.min(100, value));
  }
  async function release(session) {
    session.closing = true;
    try {
      session.sender?.stop();
      if (session.setup) {
        try {
          await session.connection.rtsp.request('TEARDOWN', {
            uri: session.connection.sessionUrl, timeoutMs: 2000
          });
        } catch { /* Always release sockets if the receiver is unavailable. */ }
      }
    } finally {
      session.connection?.close();
      session.ntp?.stop();
      if (!session.sender && session.sockets) {
        session.sockets.data.close(); session.sockets.control.close();
      }
      releaseClock(session);
    }
  }
  async function close(id) {
    const session = sessions.get(id);
    if (!session) return;
    sessions.delete(id);
    await release(session);
  }
  async function open(command) {
    await close(command.id);
    const session = { host: command.host, closing: false, setup: false };
    try {
      const capabilities = protocol.readCapabilities(command.properties ?? {});
      if (capabilities.requiresUserPairing)
        throw new Error('This receiver requires a PIN/password pairing flow that Musync does not yet support.');
      session.connection = await protocol.AirPlayConnection.open({
        host: command.host, port: command.port, identity, name: 'Musync', timeoutMs: 8000,
        onEvent: event => {
          if (!session.closing && /closed|error|dropped/i.test(event)) reportError(command.id, event);
        }
      });
      const ptp = await acquireClock(session);
      const timing = capabilities.isAppleReceiver || capabilities.supportsPtp ? 'ptp' : 'ntp';
      if (timing === 'ntp') session.ntp = new protocol.NtpTimingResponder();
      const timingPort = session.ntp ? await session.ntp.start() : undefined;
      await session.connection.setupSession('Musync', { timing, timingPort });
      session.setup = true;
      session.sockets = await protocol.RealtimeSender.bindSockets();
      const stream = await protocol.setupRealtimeStream(session.connection.rtsp, session.connection.sessionUrl, {
        audioKey: session.connection.hap.sharedSecret, localDataPort: session.sockets.dataPort,
        localControlPort: session.sockets.controlPort, streamConnectionId: Math.floor(Math.random() * 0x7fffffff)
      });
      session.sender = new protocol.RealtimeSender({
        host: command.host, ...stream, audioKey: session.connection.hap.sharedSecret, ptp, timing, leadMs: 300
      }, session.sockets.data, session.sockets.control);
      await protocol.setVolume(session.connection.rtsp, session.connection.sessionUrl, volume(command.volume ?? 0));
      await protocol.setMetadata(session.connection.rtsp, session.connection.sessionUrl,
        { title: 'Musync', artist: 'Live audio' }, session.sender.rtpPosition);
      session.sender.start();
      sessions.set(command.id, session);
    } catch (error) { await release(session); throw error; }
  }
  async function setVolume(id, value) {
    const session = sessions.get(id);
    if (!session) throw new Error('AirPlay session is not open');
    await protocol.setVolume(session.connection.rtsp, session.connection.sessionUrl, volume(value));
  }
  function sendPcm(id, pcm) {
    if (pcm.length !== 1408) throw new Error('Invalid PCM frame size');
    sessions.get(id)?.sender.sendPacket(pcm);
  }
  async function shutdown() {
    await Promise.all([...sessions.keys()].map(close));
  }
  return { open, close, setVolume, sendPcm, shutdown };
}
