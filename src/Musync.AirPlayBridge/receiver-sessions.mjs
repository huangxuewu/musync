// Inject transport resources so lifecycle tests do not bind real ports.
export function createSessionManager(protocol, identity, reportError) {
  const sessions = new Map();
  let ptp;
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
      ptp?.removePeer(session.host);
      if (sessions.size === 0) { ptp?.stop(); ptp = undefined; }
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
      if (!ptp) {
        ptp = new protocol.PtpEngine({ clockId: session.connection.identity.clockId, peers: [] });
        await ptp.start();
      }
      ptp.addPeer(command.host);
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
    for (const id of [...sessions.keys()]) await close(id);
    ptp?.stop(); ptp = undefined;
  }
  return { open, close, setVolume, sendPcm, shutdown };
}
