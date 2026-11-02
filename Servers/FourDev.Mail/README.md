# Shared game mail through Wolverine

Map and World use WolverineFx.SqlServer and WolverineFx.RuntimeCompilation **6.59.1**. Storage stays
in SQL Server: `fourdev_mail` contains inbox/outbox/agent metadata, and `fourdev_mail_transport`
contains `mail_delivery` and `mail_notifications` queues. RabbitMQ, EF Core and filesystem game-state
storage are not required.

`WolverineMailStore` implements the existing player mail, quest mail/continuation, operator reward
and World reserved-post contracts. Interactive operations use awaited typed handlers; the owning
Map/World actor still validates gameplay and applies client/inventory changes in order. This covers
ordinary letters, attachments, claims/deletion, bills/returns, cash and auction refunds, GM letters,
guild payments, lottery fallback, quests, reservations and war/arena/tournament/RPS rewards.

New `MailOperator` rewards go directly to the durable SQL transport. Its synchronous enqueue boundary
is intentional because existing game handlers may ignore the returned Task. Native header, money,
new items, permanent receipt and the `MailDelivered` outbox envelope share one SQL transaction.
`TOperatorPostOperation` remains the permanent business deduplication record after Wolverine inbox
retention or player collection/deletion. Existing SQL queue version-3 letters are forwarded with
their original operation IDs and acknowledged only after Wolverine accepts them durably.

Only World registers the notification handler/listener. It waits for the World actor's inbox drain
before completing; Map cannot recover that handler onto a node with no World actor. World resolves
the recipient's current name/main by ID and uses the existing native packet. Offline mail remains
available in the mailbox; client popups are not an exactly-once gameplay guarantee.

Apply migrations **041–046** before running the current source against the game/global databases.
Migration 046 is generated from the pinned framework's message-store/transport DDL and is repeatable.
Production has `AutoCreate.None`, no queue purge and no auto-provisioning. The explicit `provision`
switch is used only by the guarded disposable integration fixture. Keep old queue/quest/operator
receipt rows with the matching databases during rollout; do not truncate them or run two independent
issuers with new reward IDs. Start World before Maps so notifications have an actor consumer.

The Map/World Dockerfiles now build with `Servers` as their context:

```powershell
docker build -f Servers/TMapSvr.Net/Dockerfile -t mapsvr-mail Servers
docker build -f Servers/TWorldSvr.Net/Dockerfile -t worldsvr-mail Servers
```

The shared adapter does not make every game operation a single transaction. Player send/claim versus
character inventory/currency persistence, cash/auction/contract transfers, full war-result/recipient
selection and cross-Map ownership still require their own gameplay recovery boundaries. Quest child
execution remains on the Map actor with its existing SQL checkpoints and receipts. Durable failed
messages remain in Wolverine storage for inspection/replay according to its error policy.

See `Gameforge/wolverine-mail.json` and `Tools/QuestEngineCheck/WolverineMailCheck.cs` for verification
scope. Passing component tests is not a full worker/client or publisher-parity verification.

Current deployment: all five server services were rebuilt and deployed on 2026-10-30; migrations
041 through 046 are applied. See `Gameforge/deployments/2026-10-30-wolverine.json`. Startup, SQL
ownership and read-only deployed-library checks pass; real-client gameplay remains unverified.
