import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

const get = (key) => {
  const value = process.env[key];
  if (!value) throw new Error(`Missing ${key}`);
  return value;
};

const edge = new URL(get('CHAT_SMOKE_API'));
assert.equal(edge.protocol, 'https:');
const hub = new URL('/hubs/messaging', edge);
const room = get('CHAT_SMOKE_CONVERSATION');
const outsiderRoom = get('CHAT_SMOKE_FOREIGN_CONVERSATION');
const makeConnection = (token) => new HubConnectionBuilder()
  .withUrl(hub.href, { accessTokenFactory: () => token, transport: HttpTransportType.WebSockets, skipNegotiation: true })
  .configureLogging(LogLevel.None)
  .build();

const client = makeConnection(get('CHAT_SMOKE_TOKEN'));
const rejected = makeConnection('invalid-smoke-token');
const peer = process.env.CHAT_SMOKE_PEER_TOKEN ? makeConnection(process.env.CHAT_SMOKE_PEER_TOKEN) : null;
try {
  await assert.rejects(rejected.start());
  await client.start();
  await client.invoke('JoinConversation', room);
  for (const method of ['JoinConversation', 'LeaveConversation', 'SetTyping']) {
    const args = method === 'SetTyping' ? [outsiderRoom, true] : [outsiderRoom];
    await assert.rejects(client.invoke(method, ...args), /Conversation unavailable/);
  }

  if (peer) {
    await peer.start();
    await peer.invoke('JoinConversation', room);
    let expire;
    try {
      const typing = new Promise((resolve, reject) => {
        peer.on('TypingChanged', resolve);
        expire = setTimeout(() => reject(new Error('Typing event missing')), 8000);
      });
      await client.invoke('SetTyping', room, true);
      const event = await typing;
      assert.equal(event.eventVersion, 1);
      assert.equal(event.conversationId, room);
      assert.equal(event.payload.isTyping, true);
      assert.equal(event.payload.side, 'CLIENT');
      assert.equal(Object.hasOwn(event.payload, 'authorUserId'), false);
    } finally {
      clearTimeout(expire);
    }
  } else {
    await client.invoke('SetTyping', room, true);
  }

  await client.invoke('LeaveConversation', room);
  console.log('SignalR HTTPS/WSS consumer smoke passed');
} catch (error) {
  // Do not dump transport exceptions: a WebSocket URL can contain the access token.
  console.error(`SignalR smoke failed (${error.name})`);
  process.exitCode = 1;
} finally {
  await Promise.allSettled([client.stop(), rejected.stop(), peer?.stop()]);
}
