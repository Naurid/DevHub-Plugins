# GitHub (`devhub.github`)

Sign in to GitHub through Git Credential Manager, publish a repository to your account, clone your repositories,
show avatars, and lend other plugins the GitHub API. Bundled with DevHub. **Requires Git.**

Code: `plugins/GitHub/DevHub.GitHub` and `DevHub.GitHub.Core` (`GitCredentialManager`, `GitHubApi`; tested in
`tests/DevHub.GitHub.Core.Tests`).

## Using it

- **Settings → GitHub**: the GitHub accounts Git Credential Manager knows (each checked with `GET /user`), your
  picture, **Sign In…** (GCM's own window) and **Sign Out…** (confirmed).
- **Publish to GitHub…** (Git menu, the Remotes section, or Push in a repository without a remote): creates an
  empty repository on your account (private by default; "name taken" shows in the dialog), adds it as a remote and
  pushes the current branch with upstream.
- **Clone Repository…** (Git): your repositories (all pages, fuzzy search) next to "any URL".
- **Avatars**: your GitHub picture next to your own commits in All Commits (matched by user.email, your no-reply
  address or your name). Other authors show their initial: no e-mail hashes are sent to a third party.

**DevHub stores no credentials.** For each API call the plugin borrows the token from GCM (`git credential fill`,
shown in the Git Console without its output); requests go only to api.github.com. Pictures come from
avatars.githubusercontent.com without the token.

## Settings

No section of its own: the accounts are GCM's.

## For other plugins

| Offers | Hook | Used by |
|---|---|---|
| A hosting service for Git: Publish, your identity, your repositories for Clone | `IRemoteHost` (Git's) | Git |
| GitHub's REST API as the signed-in user | `IGitHubApi` (`DevHub.Integration`) | Board (GitHub Issues import), Board Git (an issue's pull requests) |

`IGitHubApi.SendAsync(GitHubApiRequest)` returns a `GitHubApiResponse` with the ETag, the next page and the rate
limit; it throws `GitHubApiUnavailableException` when nobody is signed in or GitHub can't be reached. Callers pace
themselves (send the last ETag as `IfNoneMatch`: a 304 costs no rate limit). The full contract is in DevHub's
`docs/sdk/SDK.md`.
