# Codex B06 — AUDIO/VIDEO и WebRTC signaling via SignalR [Этап 2]

Прочитай `docs/contracts/events-v1.md`, `docs/architecture.md`, `docs/security.md`, `docs/data-model.md`. Требуется B05.

## Реализуй

1. Media type `AUDIO|VIDEO`: проверка content MIME, размеров/длительностей, безопасное скачивание, metadata.
2. EF Core Call state machine (RINGING, CONNECTING, ACTIVE, ENDED, MISSED, REJECTED), TTL, one accepting operator/call, explicit conversation authorization; migration и конкурентные тесты.
3. REST `POST /v1/calls`, `GET /v1/calls/{id}`, `POST /v1/calls/{id}/end`, `POST /v1/calls/{id}/ice-config` с short-lived TURN credentials.
4. **SignalR Hub methods**: AcceptCall, RejectCall, SendOffer, SendAnswer, SendIceCandidate, HangupCall; серверные события: CallIncoming, CallStateChanged, CallOffer, CallAnswer, CallIceCandidate (проверить точный контракт). Валидировать, кто может посылать offer/ICE в каком состоянии.
5. Rate limit + payload size, no SDP/ICE in logs, no recording, no media relay through API; tests concurrent accept, suspend, expiry, hangup/reject/reconnect.

## Критерии

Backend signaling протестирован реальными совместимыми SignalR клиентами; физический звонок считается готовым только после тестов mobile+browser и TURN fallback. Никаких group calls.
