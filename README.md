# Gfs.Console.Uci.Retry

## Purpose

Console app that processes **one** UCI error file per invocation. When the error is the known geo-code failure, it corrects the AS `EQUIP.ZIP` fields of the UCI embedded in the error file, releases the corrected UCI for a single automatic retry, and archives the original error file. Anything ambiguous or unsafe fails closed to manual review.

Automation Anywhere (AA) calls it once per error file and acts on the **process exit code only** (see below). There is no folder monitoring, polling or batching.

## Solution structure

```
.build/                                  reserved for future build/deployment assets (none yet)
src/Gfs.Console.Uci.Retry/
  Program.cs, CommandLine.cs,            bootstrap: argument parsing, configuration, logging, result output
  UciRetryApp.cs, appsettings.json
  Configuration/ProcessorOptions.cs      bound from the "Processor" section
  Models/                                result, outcome, error type, embedded UCI, UCI model, retry marker
  Processing/                            classification, extraction, parsing, remediation, marker, submission, orchestration
  Notifications/                         notification options, message building, SMTP delivery, single dispatch point
tests/Gfs.Console.Uci.Retry.Tests/
  Processing/                            unit tests per component
  Notifications/                         message builder and notification service unit tests (fake sender)
  Integration/                           UciErrorProcessor lifecycle/analysis/remediation and app/executable tests
  Fixtures/                              synthetic test data (TestData, UciBuilder)
Gfs.Console.Uci.Retry.slnx
.gitignore
.local-samples/                         git-ignored, local-only real error files (never committed)
```

Namespaces match folders: `Gfs.Console.Uci.Retry` (project root files), `.Configuration`, `.Models`, `.Processing`, `.Notifications`; tests use `Gfs.Console.Uci.Retry.Tests` plus `.Fixtures`, `.Processing`, `.Notifications`, `.Integration`.

## End-to-end processing flow

1. Read the error file (ISO-8859-1, so bytes map 1:1 to characters and offsets stay byte-exact).
2. Classify the error. **GeoCode** only if the text *outside* the UCI consists solely of exact GeoCode messages (`GeoCodeErrorIdentifier`). A UCI with several assets can report one message per asset, so one or more messages are supported (they need not share an Asset ID). The `ý`-delimited wrapper fields are not guaranteed to be ordered pairs: each non-empty field must be an exact GeoCode message, a numeric/status field (digits only; any number of them, consecutive or not) or the `{contract} Contract not created.` field. That contract field must appear exactly once but may be anywhere. At least one GeoCode message is required. Any other field means every automatic remediation is refused. Zero matches, or any other failure message or unrecognized/malformed content alongside them, is `Unknown` → manual review. A wrapper with no `ý` still requires exactly one exact message. Every AS `EQUIP.ZIP` is then inspected independently by the remediator; the number of messages is not correlated with the number of AS sections.
3. Extract the embedded UCI and the identifier (diagnostic only).
4. Parse and validate the UCI structure.
5. Remediate AS `EQUIP.ZIP`, then validate the result.
6. Create the working upload, claim the retry marker, move the working upload outbound, archive the original error file.
7. Write one JSON result to stdout and return the exit code. Logs go to stderr only.

## UCI structure

The error file is wrapper/error text plus the original UCI. The UCI starts at `CU|`; the wrapper ends it with `{`, which is **not** part of the UCI and is never written outbound. The UCI is pipe-delimited and variable length:

```
CU -> RP* -> CO -> (AS -> AUC -> AMB -> AMI)* -> MB* -> MI* -> RN
```

Parsing is deterministic using the official data-field counts per section (header excluded): CU 25, RP 9, CO 142, AS 79, AUC 28, AMB 19, AMI 14, MB 19, MI 14, RN 12. Empty fields count; section-looking values inside a section's field range are ordinary data; asset sections form ordered groups. The UCI must end exactly after RN's fields, optionally followed by one final `|`. Any deviation → manual review.

## Geo-code remediation rule

`EQUIP.ZIP` is the 13th data field after the AS header (`AS.Fields[12]`, `UciLayout.EquipZipFieldIndex`) and the only field ever changed.

| Value | Action |
|---|---|
| 9 digits, or `#####-####` | replaced by the first 5 characters |
| 5 digits, or blank (`""`) | unchanged |
| anything else (including whitespace) | not guessed → manual review |

All AS sections are checked before anything changes, so one unsupported value rejects the whole UCI. Changes are exact source-range splices (never global replace). The output is rebuilt independently, compared byte for byte, reparsed, and checked field by field.

## Retry protection

Each error file gets at most one automatic retry. The marker `<errorfile-stem>.geo-retry.json` (in `RetryMarkerDirectory`) is claimed atomically with `CreateNew`/`FileShare.None`, before the corrected UCI is released. **Marker existence alone means the retry is consumed**; empty, malformed or partial markers also block another retry, and markers are never read back, repaired, overwritten or deleted automatically. This deliberately prefers manual review over any chance of a duplicate submission.

Exactly one marker is created per automatic retry, named `<original-file-base-name>.geo-retry.json` (never the UCI identifier). It holds only non-sensitive claim-time metadata (original file name, UCI identifier, error type, status `Prepared`, working file path, counts, UTC time). The status is diagnostic only, is never updated (it stays `Prepared` after a successful submission) and never affects eligibility. No companion `.geo-retry.status.json` file is created. Retry-marker retention/cleanup is outside the scope of this application; it only creates and respects the marker.

## File lifecycle

All names derive from the **original error file name**, never the contract/UCI identifier. For `ex10000001.txt`:

| File | Location | Name |
|---|---|---|
| Working corrected UCI | `RetryWorkingDirectory` | `ex10000001.working.<guid>.txt` |
| Retry marker | `RetryMarkerDirectory` | `ex10000001.geo-retry.json` |
| Outbound corrected UCI | `OutboundDirectory` | `ex10000001.txt` |
| Archived **original error file** | `ArchiveDirectory` | `ex10000001_<yyyyMMdd_HHmmss>.txt` (UTC) |

```
error file -> extract embedded UCI -> create + validate working upload -> claim marker
           -> MOVE working upload to outbound -> MOVE original error file to timestamped archive
```

- Outbound contains only the corrected embedded UCI (no error text, no `{`; a final `|` is kept only if the source had one). The archive contains the original error file, unchanged; there is no corrected-UCI archive.
- Nothing is ever overwritten. An existing outbound file is detected before the claim (exit 30, no marker).
- Exit 0 is returned only after the working file is gone, the outbound file exists and verifies, the source error file is gone, and the archived bytes equal the original.
- After the claim, any failure (exit 30) keeps the marker and any file already moved, rolls nothing back, and needs manual investigation. If the outbound move fails the working file stays and the error is not archived; if only the archive move fails the outbound file stays and the error remains at source. If the timestamped archive name already exists, the archive step fails closed.

## Configuration

`appsettings.json` (next to the executable) section `Processor`; environment variables prefixed `UCIRETRY_` override it (e.g. `UCIRETRY_Processor__OutboundDirectory`).

| Key | Purpose |
|---|---|
| `RetryWorkingDirectory` | corrected working upload (required) |
| `RetryMarkerDirectory` | retry markers (required) |
| `OutboundDirectory` | corrected UCI released for retry (required) |
| `ArchiveDirectory` | archived original error files (required) |
| `GeoCodeErrorIdentifier` | text identifying the geo-code error |
| `ErrorFileUciIdentifierPattern` | regex with named group `id` for the diagnostic identifier |

Section `Notifications` (checked-in values are non-operational: `Enabled` false, empty host/from/recipients, port 0):

| Key | Purpose |
|---|---|
| `Enabled` | master switch; false means no email is sent |
| `SmtpHost`, `SmtpPort`, `UseSsl` | SMTP relay (approved relay to be supplied externally) |
| `FromAddress` | sender address |
| `ManualReviewRecipients` | recipients for `ManualReview` and `AlreadyRetried` |
| `TechnicalRecipients` | recipients for `RemediationFailed`, `InvalidInput` and `ProcessingError` |
| `SubjectPrefix` | optional subject prefix |
| `MaxAttachmentBytes` | attachment size limit (default 5242880) |
| `IncludeUciIdentifier` | include the contract/UCI identifier in messages (default false) |

The checked-in `appsettings.json` is intentionally non-operational: the four directories are empty and there are no fallback paths. Production values must be supplied externally:

```
UCIRETRY_Processor__RetryWorkingDirectory
UCIRETRY_Processor__RetryMarkerDirectory
UCIRETRY_Processor__OutboundDirectory
UCIRETRY_Processor__ArchiveDirectory
```

Notification settings are supplied the same way, for example:

```
UCIRETRY_Notifications__Enabled
UCIRETRY_Notifications__SmtpHost
UCIRETRY_Notifications__SmtpPort
UCIRETRY_Notifications__FromAddress
UCIRETRY_Notifications__ManualReviewRecipients__0   (index 0, 1, ...)
UCIRETRY_Notifications__TechnicalRecipients__0
UCIRETRY_Notifications__SmtpUserName   (only if the relay requires authentication)
UCIRETRY_Notifications__SmtpPassword   (secret; never put it in appsettings.json)
```

A blank required directory is a configuration error (exit 50) and is detected before any file is written.

## Notifications

The application owns all email notifications once it has started; AA does not interpret the exit code or stdout to alert anyone. `UciRetryApp` sends **at most one business email** (ManualReview/AlreadyRetried) and, for **every non-zero result, exactly one technical escalation** (a bounded 2-attempt delivery that is never escalated again). Invariant: exit 0 needs no technical email; non-zero means the application attempted one. A successful business email is not followed by a technical one unless a later failure occurs (for example the archive move). Processing classes never send or format email.

| Outcome | Email | Recipients | Original error attached |
|---|---|---|---|
| `AutoRetrySubmitted` (exit 0) | none (logged only) | - | - |
| `ManualReview` (exit 0 once delivered) | manual review | `ManualReviewRecipients` | yes, if it exists and is within `MaxAttachmentBytes` |
| `AlreadyRetried` (exit 0 once delivered) | manual review: marker exists, no retry attempted, another automatic retry prohibited | `ManualReviewRecipients` | same as above |
| `RemediationFailed` (exit 30) | technical; states the stage (before claim / after claim / after outbound move / archive failed / status not recorded), whether the retry is consumed and whether the downstream process may have seen the outbound file | `TechnicalRecipients` | no |
| `InvalidInput` (exit 40), `ProcessingError` (exit 50) | technical, when notification settings are usable | `TechnicalRecipients` | no |
| Business email undelivered, or archive failed after it (exit 50) | technical (the business email is not repeated) | `TechnicalRecipients` | no |
| Failure before the normal flow (configuration unreadable, log4net cannot start; exit 50) | technical, using whatever notification settings can be read (independent of logging) | `TechnicalRecipients` | no |

Messages contain only the error file name (no directories), outcome, error classification, lifecycle flags, a UTC timestamp, a non-sensitive reason and the next action. They never contain UCI content, ZIP values, customer data, the full error file or secrets. The UCI identifier is included only if `IncludeUciIdentifier` is set. Only the original error file is ever attached (never the working/outbound UCI, marker, logs or configuration). A missing or oversized file is not attached and the body says so.

**Delivery failure** (bounded: 2 attempts, fixed 2 second pause; only the exception type is logged):
- `ManualReview`/`AlreadyRetried`: the result becomes `ProcessingError` (exit 50), the error file is left in place and not archived, and one technical escalation is attempted. This also applies when notifications are disabled, incomplete or have no recipients (fail safe; the technical attempt is then also logged as not sent).
- `RemediationFailed` after the marker claim: stays exit 30; the message states notification failed after the retry claim and the retry remains consumed. The marker and outbound file are kept and nothing is rolled back.
- Technical email delivery failure: logged, not escalated again, the original non-zero result is preserved.
- The only unavoidable gap: if neither `appsettings.json` nor `UCIRETRY_` environment variables supply usable notification settings, or the process cannot start at all, no email can be sent; the failure is logged and the exit code is non-zero.

With `Notifications:Enabled` false (the checked-in default and the test setting) no email is ever sent, so local runs of a manual-review outcome return exit 50 (the required notification cannot be delivered). Tests use a fake sender and never contact a mail server.

## Exit codes

The process exit code answers one question: **did the application successfully handle this error file?**

- **0** - the file was completely handled and the original error file was archived (timestamped, bytes unchanged) - it is no longer in the error/download directory. This does **not** mean the UCI was auto-retried: `AutoRetrySubmitted` = retry + archive; `ManualReview` = email + archive; `AlreadyRetried` = email + archive.
- **non-zero** - the application could not complete its responsibility (required notification not delivered, notifications disabled or misconfigured when needed, filesystem/marker/outbound/archive failure, post-claim failure, invalid invocation, unexpected exception).

Internally the application still distinguishes `ProcessingOutcome` values for notification selection, logs and tests. The mapping to the exit code lives in one place, `ProcessingOutcomeExtensions.ToExitCode`:

| Internal outcome | Exit | Meaning |
|---|---:|---|
| `AutoRetrySubmitted` | 0 | Corrected UCI released outbound, original error archived (no email) |
| `ManualReview` | 0 | Cannot be safely automated; manual-review email delivered, then original error archived |
| `AlreadyRetried` | 0 | Retry marker exists (left unchanged); no retry attempted; email delivered, then original error archived |
| `RemediationFailed` | 30 | Eligible failure, but remediation/validation/marker/outbound/archive failed |
| `InvalidInput` | 40 | Bad/missing argument, invalid path, missing file |
| `ProcessingError` | 50 | Technical/configuration failure, including an undelivered required notification |

If a `ManualReview` or `AlreadyRetried` email cannot be delivered, the final outcome becomes `ProcessingError` (exit 50) and the error file is **not** archived. If the email was delivered but archiving then fails, the result is also `ProcessingError` (exit 50): the source/archive state left by the failed move is preserved, the marker is untouched, nothing is retried, and **no second email** is sent (at most one business notification per invocation; no persistent email state). `Processor:ArchiveDirectory` is checked before a business email is sent, so a missing archive directory fails early without notifying.

AA never archives, moves or deletes the error file. The distinct non-zero codes are for troubleshooting; AA does not interpret them.

stdout is one diagnostic JSON object (unknown values omitted): `outcome`, `exitCode`, `errorType`, `uciIdentifier`, `message`, `asSectionsInspected`, `equipZipFieldsModified`, `originalErrorFile`, `retryUciFile`, `outboundFile`, `archivedErrorFile`, `markerFile`. It is for manual debugging, logs and tests only; AA does not read it. No result or status files are created for AA. UCI content and ZIP values are never logged or returned.
## Automation Anywhere integration boundary

AA's entire contract with this application is the **process exit code**:

```
for each error file:
    run  Gfs.Console.Uci.Retry --error-file "<full path>"
    exit code 0      -> continue to the next file
    exit code != 0   -> send ONE generic technical alert (also if the executable cannot launch), then continue
                        per AA's existing orchestration/error handling
```

The application owns the complete per-file disposition: classification, extraction, parsing, correction, retry marker, outbound release, archiving the original error file, and all business and operational notifications.

AA does **not**:
- read UCI or error files
- parse stdout JSON or inspect `ProcessingOutcome`
- understand GeoCode, manual review, already-retried or retry markers
- send manual-review/business emails
- move or delete a successfully processed error file (the application already archived it before returning 0)
- need to interpret which non-zero code was returned
## Running, building and testing locally

```
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Gfs.Console.Uci.Retry -- --error-file "C:\path\to\ex10000001.txt"
```

`--error-file` must appear exactly once with a fully qualified path to an existing file. Point the four directory settings at scratch folders (e.g. via `UCIRETRY_Processor__*` environment variables) when experimenting; a successful run **moves** the error file to the archive.

## Logging

Processing classes use `ILogger`; log4net is plugged in underneath it (`Microsoft.Extensions.Logging.Log4Net.AspNetCore`, which uses log4net 3.x). stdout is reserved for the single JSON result and never receives log output.

| Setting | Environment variable | Meaning |
|---|---|---|
| `Logging:Log4Net:Enabled` | `UCIRETRY_Logging__Log4Net__Enabled` | `false` by default. `true` turns on daily file logging. |
| `Logging:Log4Net:Directory` | `UCIRETRY_Logging__Log4Net__Directory` | Required when enabled. Never hard-coded; supply it externally. |

- **Development / tests:** file logging disabled; logs go to stderr only. No directory is needed.
- **Deployed:** logs go to stderr and to `<Directory>\UciRetry_yyyyMMdd.log` (log4net `RollingFileAppender`, `rollingStyle=Date`, `staticLogFileName=false`, `appendToFile=true`, `MinimalLock`). A restart appends to the same day's file. Appender and layout live in `log4net.config` (copied to build and publish output); only the directory comes from configuration.
- **Layout:** `2026-11-05 11:22:13.145 INFO  UciRetry - message` (exceptions follow on the next lines). The logger name is the single shared `UciRetry` category.
- **Startup failure:** if file logging is enabled and the directory is blank, cannot be created or written, `log4net.config` is missing, or log4net reports a configuration error, the application writes a `ProcessingError` JSON to stdout, logs to stderr, and exits 50 (AA's generic tech alert).
- **Permissions:** the service account needs create/write/modify on the log directory and read on the application folder.
- **Retention limitation:** log4net's date-based rolling does not delete old files. No custom cleanup is built; use an external housekeeping job.
- **Content:** logs contain filenames (no directory), classification, outcome, counts, lifecycle stages and exception type only; never UCI content, ZIP values, customer data, SMTP secrets or email bodies.
## Data hygiene

Production or sample error payloads (real UCI/customer data) must never be committed. Tests use only synthetic fixtures built in code. Keep real samples under `.local-samples/` (git-ignored) or outside the repository.
