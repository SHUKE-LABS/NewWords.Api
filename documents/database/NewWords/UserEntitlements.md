# Database: NewWords Table: UserEntitlements

Server-side subscription entitlement, one row per user (issue #37). A user is premium iff
`PremiumExpiresAt` is non-null and greater than the current unix time. `Store` and
`OriginalTransactionId` are populated by the store-receipt verification ticket.

 Field                 | Type        | Null | Default | Comment
-----------------------|-------------|------|---------|---------
 Id                    | bigint      | NO   |         | Primary key, auto-increment
 UserId                | int         | NO   |         | Owning user (unique)
 PremiumExpiresAt      | bigint      | YES  | NULL    | Unix seconds; premium iff non-null and > now
 Store                 | varchar(32) | YES  | NULL    | Granting store (e.g. appstore, playstore)
 OriginalTransactionId | varchar(255)| YES  | NULL    | Store transaction id for renewal/refund correlation
 CreatedAt             | bigint      | NO   |         | Unix seconds
 UpdatedAt             | bigint      | NO   |         | Unix seconds

## Indexes:

 Key_name                   | Column_name | Seq_in_index | Non_unique | Index_type | Visible
----------------------------|-------------|--------------|------------|------------|---------
 PRIMARY                    | Id          |            1 |          0 | BTREE      | YES
 UQ_UserEntitlements_UserId | UserId      |            1 |          0 | BTREE      | YES
