## Stub Database
When a Silo is encrypted, its sensitive databases are wrapped in an inner encrypted archive. A reduced, (semi-)unencrypted `z.db` — the **stub** database — sits in the outer archive instead, holding just enough to identify the Silo and unlock it.

### Tables
| Table | Description |
|--|--|
| `Password` | Key material protected by a password (`Sid`, `Material`). |
| `UserKey` | Key material protected by a user's public key (`Sid`, `Material`). |

The stub also carries a copy of the `Header` singleton (see [Global Database](GlobalDb)) so the Silo can be identified before it's unlocked.

### See Also
* [Global Database](GlobalDb)
* [Data](Data)
