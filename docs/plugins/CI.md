# CI (`devhub.ci`)

GitHub Actions for the repositories you have open: the run history, a mark that turns green or red as soon as a run
passes or fails, the failed step's log, and re-running a job. **Requires GitHub** (its `IGitHubApi` makes the calls
as the account signed in through Git Credential Manager; Git comes with it). Not bundled: install it from the
marketplace. In the Git client preset.

Code: `plugins/Ci/DevHub.Ci.Core` (runs and jobs from the API's JSON, the GitHub remote, the polling pace, finished-run
detection, log excerpts; no Avalonia, no network; tested in `tests/DevHub.Ci.Core.Tests`) and `plugins/Ci/DevHub.Ci`
(the polling service, the tool window, the status-bar mark, the log tabs).

## Using it

- **Which repositories:** the ones open as repository tabs whose `origin` is on github.com. Nothing else is polled.
- **CI tool window** (bottom, next to the Git Console): the runs of the repository tab in front, newest first. Each
  row shows the state (✓ passed, ✗ failed, ● queued or running, ⊘ cancelled), the commit message, the workflow and
  run number (with the attempt after a re-run), branch, event, who started it, how long it took and when. Filter by
  branch and workflow; **Load more** fetches older runs; **Refresh** asks GitHub now.
- **A run's jobs:** the arrow expands a run: each job with its state and duration, the steps that failed, **Show
  Log**, **Re-run** and **Open in Browser**. A failed run has **Re-run failed** (its failed jobs and the jobs that
  depend on them).
- **Logs:** Show Log opens a tab with the **failed steps** of the job's log: each step's start as a heading, the lines
  GitHub marks as errors in red, timestamps gone. The switch shows **all of the log**. A log without error markers
  shows its last 200 lines. Logs expire on GitHub after a while; the tab says so.
- **Real time, visually:**
  - the status bar shows **✓ CI**, **✗ CI** or **● CI** (green, red, accent) for the branch checked out in the tab in
    front: its newest commit's runs, the latest attempt of each workflow. The tooltip lists them; a click opens the
    tool window;
  - when a run you're watching finishes, the status bar says so ("✗ CI failed on main · DevHub"); it's in the
    status bar's history too (a click on the message);
  - Git's commit list and branch tree show ✓ ✗ ● next to commits and branches that ran (through `IGitDecorator`);
    a click opens the run on GitHub.
- **Re-runs ask first** and say in the status bar how it went. Re-running needs the GitHub account's `repo` and
  `workflow` rights, which a Git Credential Manager sign-in normally has; if GitHub refuses (403), sign in again in
  Settings → GitHub.

## How it stays light

- **One request per watched repository per poll:** `GET repos/{owner}/{repo}/actions/runs?per_page=20` with the last
  ETag. When nothing changed GitHub answers 304, which costs no rate limit.
- **The pace follows what's happening** (`PollSchedule`, tested): every 5 s for a minute after you push from DevHub
  (the new run appears within seconds), every 15 s while a run is queued or running, every 2 minutes otherwise; every
  5 minutes when fewer than 200 requests are left this hour (other tools share the limit).
- **One background loop** for every repository; the UI only hears about changes.
- **Jobs are loaded only for a run you expand** (with their ETag; kept until the run changes); **logs only when you
  open one** (kept once the job is done: a finished log never changes).
- **The repository's GitHub address and branch are read from `.git/config` and `.git/HEAD`**: no git process.
- **The token isn't fetched per call:** the GitHub plugin keeps it in memory for 10 minutes (`CredentialCache`).
- **No news flood:** runs that finished before DevHub started (or before the repository was first polled) are
  history. Only runs that finish while DevHub watches, or that start and finish between two polls, are announced.
- No settings, nothing saved: what it knows lives in memory.

## Actions

| Id | Text | Menu |
|---|---|---|
| `Ci.Refresh` | Refresh CI Runs | CI |
| `Ci.RerunFailed` | Re-run Failed Jobs… (the current branch's latest failed run) | CI |

Tool window: `ci` (bottom, Order 110). Documents: `CiLog:<folder>|<run>|<job>|<job name>` (one per job, restored at
startup).

## For other plugins

| Offers | Hook | Used by |
|---|---|---|
| ✓ ✗ ● marks for commits and branches | `IGitDecorator` | Git |

| Uses | From | For |
|---|---|---|
| `IGitHubApi` (required: Requires GitHub) | GitHub | every call |
| `IRepositoryEvents` (optional) | Git | polling quickly after a push; the status mark after a branch switch |
| `IDocumentService` | the shell | which repository tabs are open and in front, the log tabs |

## Diagrams

`docs/diagrams/CI/`: use cases, the activity of a run from push to re-run, and the classes.
