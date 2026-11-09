# NightlyBatch sample

A console app for scheduled, unattended runs. It reads a list of tasks from
[`batch.json`](batch.json) and runs them one after another, each in its own working directory.
Each result goes into a timestamped folder, with a summary, and the exit code tells the
scheduler whether the night went well.

The example batch reviews this repository's own folders and writes a short report for each,
without changing any files. Replace the tasks with your own: one prompt over many repos, or a
different prompt per task.

## What a run produces

```
runs/2026-11-10_020000/
  summary.md        one row per task: status, time, turns, cost, rate-limit waits, note
  core-library.md   Claude's final reply for each task
  extensions-ai.md
  samples.md
  run.log           the same timestamped lines the console shows
```

- **One task failing does not stop the batch.** It is marked `failed` (or `timed-out`) with a note,
  and the next task starts.
- **Ctrl+C or SIGTERM** (`systemctl stop`, `docker stop`) cancels the task in progress, killing its
  Claude Code process tree. The remaining tasks are marked `skipped`, and the summary is still
  written.
- **Runs never overlap.** A run holds `runs/.lock` for its whole duration. If last night's run is
  still going, tonight's exits at once with code 2. The OS releases the lock even if the process
  is killed.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Every task succeeded |
| 1 | At least one task failed or timed out |
| 2 | The batch file is missing or invalid, or another run holds the lock |
| 3 | The Claude Code CLI was not found |
| 130 | Canceled (Ctrl+C or SIGTERM) |

## The batch file

Comments and trailing commas are allowed. Relative paths are relative to the batch file's
folder, so the scheduler can start the app from any directory.

| Setting | Meaning |
| --- | --- |
| `outputDirectory` | Where run folders go (default `runs`) |
| `defaults.prompt` | Prompt for every task without its own |
| `defaults.model` | Model id, e.g. `sonnet`. Null = the CLI's default |
| `defaults.timeout` | Per-task limit, e.g. `01:00:00`. Null = none. Rate-limit waits count toward it |
| `defaults.skipPermissions` | `false` (the example's choice) allows read-only tools only. `true` lets Claude run Bash and edit files |
| `tasks[]` | `name` (also the result file name) and `workingDirectory`, plus any of `prompt`, `model`, `timeout`, `skipPermissions` |

The file is validated before anything runs. Every problem (a missing directory, a task with no
prompt, a duplicate name) is listed, and the app exits with code 2.

## Rate limits

Every task runs with `RateLimitWait = RateLimitWaitPolicy.Default`. When a task hits a usage
limit, ClaudeCode.Net waits for the reset and **resumes the same session**, and the log says how
long it is waiting. The summary counts the waits per task. A limit further away than the policy
allows (more than 6 hours, or a fourth wait) fails that task with the reset time in its note.

A night is a good fit for this: the five-hour subscription window has usually reset by the
time you are back. Keep a task's `timeout` longer than a possible wait, or leave it null.

## Run it by hand

Requires the Claude Code CLI installed and signed in (`claude login`).

```sh
cd samples/NightlyBatch
dotnet run                       # uses batch.json in this folder
dotnet run -- path/to/other.json
```

## Schedule it

Publish once, put a batch file next to the app (or anywhere), and point the scheduler at both:

```sh
dotnet publish samples/NightlyBatch -c Release -o C:/tools/nightly    # or ~/nightly on Linux
```

**Windows Task Scheduler**, daily at 02:00:

```bat
schtasks /Create /TN "Claude nightly batch" /SC DAILY /ST 02:00 ^
  /TR "\"C:\tools\nightly\NightlyBatch.exe\" \"C:\tools\nightly\batch.json\""
```

**Linux cron** (`crontab -e`), daily at 02:00, console output appended to a log:

```cron
0 2 * * * $HOME/nightly/NightlyBatch $HOME/nightly/batch.json >> $HOME/nightly/cron.log 2>&1
```

What usually goes wrong with a scheduled run:

- **The task must run as the account that ran `claude login`.** The CLI keeps its credentials in
  that user's profile, so a task running as SYSTEM, or a different user, is not signed in. On
  Windows, "Run whether user is logged on or not" still works, as long as it is that account.
- **PATH is minimal under cron and Task Scheduler.** ClaudeCode.Net also checks the usual
  install locations (`~/.local/bin`, the npm global folder, and so on), so the CLI is normally
  found anyway. If a run exits with code 3, add the CLI's folder to the PATH in the crontab or
  task.
- **Check the summary, not only the exit code.** A task can succeed and still report findings
  you want to see. `summary.md` links each task's reply.
