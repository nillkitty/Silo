# Startup flow

`Program.Main` asks `Hosting/StartupPlanner` what to do *before* any WPF object exists, so a launch that only hands its
document to a running instance costs a few milliseconds and never flashes a window.

```
Start -> Args?
  none  -> Is a stock instance running?
             yes -> activate it (pipe: ACTIVATE) -> this process exits
             no  -> stay open as the stock instance
  path  -> Silo type? (SiloProbe, no extraction)
             Solo   -> open in Solo mode                      (own process)
             Normal -> /P given? yes -> open in Solo mode
                                 no  -> stock instance running?
                                          yes -> pipe: OPEN <path> -> it activates -> this process exits
                                          no  -> open here, stay open as the stock instance
  URL   -> open in Remote mode                                (own process)

From the stock instance, File > Open or drag/drop:
  Normal Silo   -> open in this instance
  Solo or URL   -> launch a new Silo process with the document as its argument (back to Start)
```

* **Stock** = the one multi-document process per Windows user (named mutex `Local\Silo.Stock.<SID>`, named pipe
  `Silo.Stock.<SID>`, current-user-only). Solo and Remote processes never take the mutex.
* **Solo** = a single-document process. A Silo is Solo when it is an archive containing an `assemblies/` folder (or a
  `.solo` marker), or a simple (single-file) Silo whose SQLite header has `application_id = 'SILO'` and bit 0 of
  `user_version` set. `SiloProbe` reads those without extracting or opening the database.
* **Remote** = a single-document process for a Silo that comes from a URL (see `Remote/`).
* `/P` (also `-p`, `--solo`) forces Solo for a normal Silo.
* If the stock instance holds the mutex but does not answer within 5 s, the launch opens the document itself rather than
  losing it.
