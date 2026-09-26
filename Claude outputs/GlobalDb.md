## Global Database (`z.db`)
The Global database is the only file required in every Silo. It holds the Silo's identity, its users, and everything configured directly by the user rather than synced from a connected source.

### Header
A singleton table (one row) holding the Silo's own identity: its file `Type`, `Created`/`Modified`/`Opened`/`LastCheck` timestamps, `Version`/`Build`, and `Creator`/`Owner`.

### Tables
| Table | Description |
|--|--|
| `User` | The identity of each user that's been authorized to open (and has successfully opened) the Silo. Captures `Domain`, `Name`, and OS `SID`, plus whether the entry is a `Group`. |
| `Connection` | A configured connected system — one row per destination (`DisplayName`, `SourceUri`). |
| `Location` | A stored location (`Uri`, `DisplayName`). |
| `Node` | Navigation tree nodes as configured by the user — one row per node (`Name`, `Uri`, `IconUri`, `Expanded`). Wrapped at runtime by a `SiloNode` view-model that adds parent/child navigation and expansion state. |
| `Pref` | A user preference — one row per preference key (`Key`/`Data`). |
| `Secret` | A stored secret blob — one row per record (`Key`/`Data`, opaque bytes). |
| `File` | Other files bundled in the Silo — one row per file (`Filename`, `Type`, `ExpectedSize`, `Crc32`). |
| `Window` | Tracks window position and size — one row per window type. |
| `Audit` | Tracks changes — one row per audited event (`TimeUtc`, `User`, `Message`). |
| `Authorization` | A delegated authorization granted to a user (`AuthorizedUser`, `AuthorizingUser`, `Level`). |
| `Assembly` | A reference to an assembly to be loaded at load time (`AssemblyName`, `SourceUri`, `Critical`). |

Most of these (all but `Header` and `User`) descend from a common `TimedUserEntity` shape, so each row also carries the `User` who owns it plus `Created`/`Modified` timestamps.

### Enums
**`SiloFileType`** — what kind of file/database a `File` (or the Silo's own `Header`) entry is: `Content`, `StubDb`, `GlobalDb`, `UniversalDb`, `MetaverseDb`, `WorldDb`, `TempDb`.

**`SiloUserLevel`** *(flags)* — access level granted by an `Authorization`: `None`, `Read`, `Write`, `Author`, `Develop`.

### See Also
* [Data](Data)
* [Stub Database](StubDb)
* [Multi-User](Multi-User)
