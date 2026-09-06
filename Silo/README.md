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
* **UserKey** record(s) - the key for the encrypted archive, encrypted using the public key of a user from one of their user certificates, salted by their SID.
 
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

* var
* table_var
* func_var
* ref_var
* pasted
* ref


### World Database(s)
World databases (**w-*.db**) store cached and/or mirrored data in the source schema of the system it was read from.  All data read via connectors is read into a world database for manipulation.


### Metaverse Database
The metaverse database (**y.db**) is used to define and store the aggregate, normalized version of objects which are derived from multiple sources.

* mv_class
* mv_attribute
* mv_class_attribute
* mv_obj

### Universal Database
The universal databse (**u.db**) stores data which is authoritative outside of the Silo and all connected worlds;  ambient data which theoretically is the same for everyone, such as the list of all Zip codes, Area codes, or US states.

