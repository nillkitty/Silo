## Temp Database (`x.db`)
The Temp database is scratch/session state — things that make working in a Silo convenient across a session but don't belong in the "real" saved data. It's pruned more aggressively than the other databases.

### Tables
| Table | Description |
|--|--|
| `Scalar` | A user variable (`Name`, `Type`, `Value`). |
| `Table` | A named table variable (`Name`). |
| `Function` | A named function variable (`Name`). |
| `Synonym` | A named alias pointing at a `Uri` `Target`. |
| `Pasted` | Data pasted in (`Source`, `Type`, raw `Data`). |
| `ReplStack` | Push/pop history of the current Data CLI location, as a `Uri`. |
| `OpenFile` | A file that was previously open (`FileUri`, `ProviderType`). |
| `OpenFileMru` | Most recently used open files (`FileUri`). |
| `VariableMru` | Most recently used variable names. |
| `AssemblyMru` | Most recently loaded assemblies (`AssemblyName`, `SourceUri`). |
| `ColorMru` | Most recently used colors (`Red`/`Green`/`Blue`/`Alpha`). |
| `TextMru` | Most recently used text snippets, grouped by `Kind`. |
| `PeopleMru` | Most recently referenced people (`PersonEid`). |

Every table here descends from `TimedUserEntity`, so each row also carries the owning `User` and `Created`/`Modified` timestamps.

### See Also
* [Data](Data)
* [App](App)
