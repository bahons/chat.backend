import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
const base = new URL(process.env.CHAT_B06_API);
assert.equal(base.protocol, 'https:');
const room = process.env.CHAT_B06_ROOM;
const tokens = ['CHAT_B06_CLIENT', 'CHAT_B06_STAFF', 'CHAT_B06_OTHER'].map((key) => process.env[key]);
const hubs = tokens.map((token) => new HubConnectionBuilder()
  .withUrl(new URL('/hubs/messaging', base).href, {
    accessTokenFactory: () => token, transport: HttpTransportType.WebSockets, skipNegotiation: true,
  }).configureLogging(LogLevel.None).build());
const [client, staff, other] = hubs;
let phase = 'connect';
async function rest(path, index, body) {
  const response = await fetch(new URL(path, base), {
    method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: 'Bearer ' + tokens[index], 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  assert.equal(response.status, 200);
  return response.json();
}
function event(hub, name, accept) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { hub.off(name, handle); reject(new Error('Expected B06 event')); }, 10000);
    function handle(value) {
      if (!accept(value)) return;
      clearTimeout(timer);
      hub.off(name, handle);
      resolve(value);
    }
    hub.on(name, handle);
  });
}
try {
  phase = 'https';
  assert.equal((await fetch(new URL('/health/live', base))).status, 200);
  await rest('/v1/me', 0);
  phase = 'connect';
  await Promise.all(hubs.map((hub) => hub.start()));
  phase = 'create';
  const incoming = event(staff, 'CallIncoming', () => true);
  const call = await rest('/v1/calls', 0, { conversationId: room, kind: 'AUDIO', clientCallId: crypto.randomUUID() });
  assert.equal((await incoming).payload.callId, call.id);
  phase = 'accept';
  const accepts = await Promise.allSettled([staff.invoke('AcceptCall', call.id), other.invoke('AcceptCall', call.id)]);
  assert.equal(accepts.filter((result) => result.status === 'fulfilled').length, 1);
  const winner = accepts[0].status === 'fulfilled' ? staff : other;
  const loser = winner === staff ? other : staff;
  const index = winner === staff ? 1 : 2;
  phase = 'signal';
  await assert.rejects(loser.invoke('SendAnswer', call.id, 'v=0\r\n'));
  const offer = event(winner, 'CallOffer', (e) => e.payload.callId === call.id);
  await client.invoke('SendOffer', call.id, 'v=0\r\ns=js-fixture\r\n');
  assert.equal((await offer).payload.sdp, 'v=0\r\ns=js-fixture\r\n');
  const answer = event(client, 'CallAnswer', (e) => e.payload.callId === call.id);
  await winner.invoke('SendAnswer', call.id, 'v=0\r\ns=answer\r\n');
  await answer;
  const ice = await rest('/v1/calls/' + call.id + '/ice-config', index, {});
  assert.ok(ice.iceServers[0].credential);
  const candidate = event(winner, 'CallIceCandidate', (e) => e.payload.callId === call.id);
  await client.invoke('SendIceCandidate', call.id, {
    candidate: 'candidate:1 1 UDP 1 192.0.2.1 5000 typ host', sdpMid: '0', sdpMLineIndex: 0,
  });
  await candidate;
  phase = 'reconnect';
  await winner.stop();
  await winner.start();
  assert.equal((await rest('/v1/calls/' + call.id, index)).state, 'ACTIVE');
  const recovered = event(winner, 'CallOffer', (e) => e.payload.callId === call.id);
  await client.invoke('SendOffer', call.id, 'v=0\r\ns=reconnect\r\n');
  await recovered;
  await winner.invoke('SendAnswer', call.id, 'v=0\r\ns=reconnected\r\n');
  phase = 'end';
  await winner.invoke('HangupCall', call.id);
  assert.equal((await rest('/v1/calls/' + call.id, 0)).state, 'ENDED');
  console.log('B06 JS HTTPS/WSS passed: race, offer/answer/ICE, peer isolation, TURN credentials, reconnect and hangup.');
} catch (error) {
  // Transport diagnostics may contain URLs or credentials.
  console.error('B06 JS consumer verification failed during ' + phase + ' (' + error.name +
    ', status=' + (error.statusCode ?? 'none') + ', code=' + (error.code ?? error.cause?.code ?? 'none') + ').');
  process.exitCode = 1;
} finally {
  await Promise.allSettled(hubs.map((hub) => hub.stop()));
}

