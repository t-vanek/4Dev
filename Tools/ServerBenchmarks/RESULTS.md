# Local validation — 25 October 2026

Windows x64, .NET SDK 10.0.402, SQL Server 2022 in the existing local Docker installation.
These are local synthetic checks, not production capacity claims.

## Map sending

4,000 encrypted 20-byte packets queued before the sender starts:

- 32 socket sends (128-packet bound), compared with one send per packet in the original loop.
- 1,000 writes of `town square` into a pre-sized PacketWriter: 0 temporary allocated bytes.

The socket regression tests additionally decrypt the received packets, check per-packet sequence numbers/order,
verify broadcast and caller-buffer isolation, and verify final-reply draining on close.

## Login database bursts

Three independent scratch databases, each with the baseline login schema/indexes and 400 synthetic accounts.
Every burst authenticates all 400 accounts with the real `TCheckIP` and `TLogin` procedures; all 4,800 measured attempts succeeded.
The scratch database preserves compatibility level 100 and collation Latin1_General_CI_AS_KS. The SQL connection pool limit is 100.
The connection pool and procedure plan are warmed before each burst. Waiting for admission is included in latency.
Medians of three runs:

| Indexes | Concurrent DB sequences | Total | p50 | p95 |
|---|---:|---:|---:|---:|
| Baseline | 100 | 189 ms | 96 ms | 174 ms |
| Baseline | 32 | 160 ms | 77 ms | 152 ms |
| Migration 024 | 100 | 160 ms | 85 ms | 149 ms |
| Migration 024 | 32 | 210 ms | 71 ms | 196 ms |

The default admission limit is 100: after indexing, lowering it to 32 improved p50 but worsened p95 and total
burst time on this setup. At the same concurrency of 100, median p95 improves by approximately 14%.
The tighter, warmed measurements above supersede the initial cold-pool runs. Re-run on the target hardware
and database size before changing the limit.

The benchmark creates and removes its own tables/database and does not copy player rows or real account credentials.
