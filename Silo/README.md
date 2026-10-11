## Silo
### Format
A silo file is a zipped folder containing several
SQLite databases.  Some of the files that may be found
in a Silo are:

| File | Format | Description
|------|--------|--------------------|
| u.db | SQLite | Universal Database |
| w.db | SQLite | World Database(s)  |
| x.db | SQLite | Scratch/Temp Database |
| y.db | SQLite | Metaverse Database |
| z.db | SQLite | Global Database  |

### Storage formats
A Silo is stored in one of two formats; both are `.silo` files and open the same way (the first bytes say which):

| Format | On disk | Use for |
|--------|---------|---------|
| **Simple** (default for new Silos) | One bare SQLite file. Every database below is a set of prefixed tables in it: `Header`, `User`, ... (z), `x_Scalar` (x), `u_Unit` (u), `y_MvType` (y), `w_<key>_World` (a World). | Most Silos. |
| **Archive** | A zip of `z.db`, `x.db`, `u.db`, `y.db`, one `w-<key>.db` per World (max 7) and optionally an `assemblies/` folder. | Big Silos; Silos that carry .NET assemblies. |

A simple Silo that grows past a threshold (author setting, 64 MB) is *offered* conversion to the archive format (File menu:
Silo > Convert to Archive Format). It is never converted silently.

**The original file is never locked.** A Silo is edited in a private working copy (a copy of the single file, or the archive
extracted to `%TEMP%\SiloWorking`); the original is only read with shared access and replaced atomically on save, so other
programs can open, copy, sync or back it up while it is open. Before saving, the file is checked for changes made by someone
else; what happens then is an author setting (prompt - the default, with "Don't ask me again" -, always overwrite, or always save
a "conflicted copy").

**Every save is recorded** in the Silo's `SaveLog` table, the same way for both formats: time, save counter
(`Header.Version`), format, user, machine and the SHA-256 of the file as it was before the save (a file cannot contain its own
hash, so each row carries its predecessor's). The saved file's last-write time is set to exactly the logged time. The same
record is raised as an event after each save.

**Solo Silos** must run in their own process: an archive containing an `assemblies/` folder (or a `.solo` marker file), or a
simple Silo whose SQLite header has `application_id = 0x53494C4F` ("SILO") and bit 0 of `user_version` set. This is readable
without extracting or opening the database. See `Hosting/STARTUP-FLOW.md`.

**Remote Silos** (`silo.exe https://host/path/my.silo`): opened by a conditional GET (only a changed file is downloaded) into a
local cache, saved locally, then uploaded with PUT. If the remote changed meanwhile it is a conflict (same author setting). Git
URLs are recognised but not implemented yet (`Remote/GitSiloRemote.cs`).

### Encryption
#### Unencrypted Silos
Unencrypted silos are a single level archive containing
all of the relevant databse files, with no encryption (except
the cipher used for **Secret** blobs such as connection credentials).

* my_silo.silo  [zip]
  * z.db
  * x.db
  * u.db

#### Encrypted Silos
Encrypted silos are structured so that the bulk of the data is stored in an encrypted, inner-archive (named 'encrypted') along with a stub header database in the outer (unencypted) archive:

* my_silo.silo  [zip, no pwd]
  * z.db        [stub]
  * encrypted   [zip, secured]
	* z.db
	* x.db
	* u.db

#### The Stub Database
The stub database must contain exactly 1 of:

* **Header** record

And can contain any of:

* **Password** record(s) - the key for the encrypted archive, encrypted with a human-enterable password which can be used to unlock the Silo.
* **UserKey** record(s) - the key for the encrypted archive, encrypted using the public key of a user from one of their user certificates, salted by their Sid.
 
### Global Index
**z.db** is the only required database in a silo and it
contains the Silo's top level indexes and cross-references, as well as the silo's schema, audit log, and metadata, 
e.g. for synchronization.

* Header
* User
* File
* Connection
* Location
* Pref
* Secret
* Node
* Assembly
* Window
* Audit
* Authorization

### Scratch Database
The scratch database (**x.db**) is where all session information, such as variables, temporarily defined functions, pasted data,
and any other trasient data.

* Scalar
* Table
* Function
* Synonym
* Pasted
* ReplStack
* OpenFile
* OpenFileMru
* VariableMru
* PeopleMru
* ColorMru
* TextMru
* AssemblyMru

### World Database(s)
World databases (**w-*.db**) store cached and/or mirrored data in the source schema of the system it was read from.  All data read via connectors is read into a world database for manipulation.


### Metaverse Database
The metaverse database (**y.db**) is used to define and store the aggregate, normalized version of objects which are derived from multiple sources.

* mv_class
* mv_attribute
* mv_class_attribute
* mv_obj

### Universal Database
The universal databse (**u.db**) stores data which is authoritative outSide of the Silo and all connected worlds;  ambient data which theoretically is the same for everyone, such as the list of all Zip codes, Area codes, or US states.

