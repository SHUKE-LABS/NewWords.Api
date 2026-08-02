# NewWords.Api

An application that helps people learn new words in foreign languages
你的词汇教练

## Local Development

For local development setup with Docker Compose (MySQL, Redis, Seq), see [README.development.md](README.development.md).

这是一个帮你更快掌握一门外语的一个App：它记录你的生词，帮助你记住它的拼写、发音，以及更重要的，用法。

你不需要一个长长的生词表。你应该尽量把新加入的生词在一个月内把它从生词表里挪走，也就是说牢固的记住它，它对你不再是一个生词。

所以这个app有三个列表

生词表
半生不熟的单词表
熟词表


这个app要先问你的母语并保存你的母语。
这个app要问你正在学习哪个语言，新加入的生词就会保存到这个语言的生词表里。

## Subscription & Free Word Cap

Subscription entitlement is enforced server-side; client-reported purchase state is never
trusted (issue #37). A user is **premium** iff their `UserEntitlements.PremiumExpiresAt` is
non-null and still in the future; otherwise they are **free**.

- **Free plan cap:** a free user may keep up to `Subscription:FreeWordCap` saved words
  (default `500`). The cap is read from config (Redis/ConfigManager or `appsettings.json`) on
  every check, so it can be changed **without a redeploy**.
- **Enforcement:** `POST /Vocabulary/Add` blocks a free user at the cap **before any LLM call**
  when the add would create a genuinely new saved word — including a word already cached by
  other users. The block returns the distinct business error code `42901`
  (`EntitlementConstants.FreeWordCapReachedErrorCode`) so the client can show a paywall rather
  than a generic failure. Re-adding a word you already own (and the pending "Generate now"
  retry) is never blocked. Premium users have no cap.
- **Legacy accounts:** the cap applies uniformly to every non-premium account, including
  accounts created before issue #37. Existing words are retained, but a non-premium account at
  the cap is blocked from genuinely new adds with `42901`. There is no legacy backfill or
  separate cap-exemption state. User 1's existing over-cap account is covered by an explicit,
  durable/long-term manual premium grant as an operational exception, not by grandfathering.
- **Policy revisit:** reopen the grandfathering question if a future bulk import reintroduces
  pre-cap accounts.
- **Status endpoint:** `GET /Entitlement/Status` (authenticated) returns
  `{ plan, premiumExpiresAt, savedWordCount, wordCap }` so the client can render the plan and an
  `X/cap` indicator.

Store receipt verification (which populates the entitlement row) is a separate ticket.
