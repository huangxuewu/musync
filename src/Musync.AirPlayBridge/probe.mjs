import { AirPlayConnection } from '@sonn-audio/node-airplay';
const host = process.argv[2];
const port = Number(process.argv[3] ?? 7000);
if (!host || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Usage: probe.mjs host port');
let connection;
try {
  connection = await AirPlayConnection.open({host, port, name:'Musync', timeoutMs:8000});
  const info = await connection.refreshInfo();
  // Verify encrypted control only. No stream, timing ports, volume or playback.
  process.stdout.write(JSON.stringify({connected:true,encryptedControlVerified:true,model:info.model ?? null}) + '\n');
} catch (error) {
  process.stdout.write(JSON.stringify({connected:false,error:error.message}) + '\n');
  process.exitCode = 1;
} finally { connection?.close(); }
