# Log message generator

Single source of truth for the English (invariant) log message catalog and the
`EventId` code map used across the solution.

`logmessages.tsv` — the master. Tab-separated: `Name <TAB> EventId <TAB> Message`.
Lines starting with `#` are comments. The `EventId`'s thousands digit picks the
owning project (Core `1xxx`, Infrastructure `2xxx`, Search `3xxx`, Smb `4xxx`,
Web `5xxx`, Host `6xxx`). Messages use **named** structured placeholders, e.g.
`Share sync failed for {Share}`.

From this, `gen_logs.py` regenerates three committed files in
`src/Kaimo_File_Server.Core/Logging/`:

- `LogEvents.cs` — the `EventId` catalog (`LogEvents.SmbStarted`, …).
- `LogMessages.resx` — the embedded English message templates.
- `LogMessages.Designer.cs` — the strongly-typed accessor (`LogMessages.SmbStarted`).

These three are marked *auto-generated* — do not hand-edit them. To add or change a
log message: edit `logmessages.tsv`, then run:

```
python tools/loggen/gen_logs.py
```

(The Visual Studio ResX code generator is not run on a CLI `dotnet build`, which is
why `LogMessages.Designer.cs` is generated here and committed.)

Usage at a call site:

```csharp
_logger.LogError(LogEvents.SmbShareSyncFailed, ex, LogMessages.SmbShareSyncFailed, name);
```

The numeric code shows up in the log line (`[4103]`) — grep `LogEvents.cs` to find the
exact call site / owning subsystem.
