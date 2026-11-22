# Versions

Rivalry Core follows the ecosystem version policy ([GutenStructure docs/versioning.md](https://github.com/TheIvoryDisaster/GutenStructure/blob/main/docs/versioning.md)).
Dependents cap their range at the next minor, and Meitou refuses a dependent whose dependency, optional ones included, loads out of range,
so a minor bump knocks every dependent out until each ships a release.

- **Minor** (`0.6.x` to `0.7.0`) only for a change to the dependent contract: a member listed in `RivalryCore.Tests/DependentContract.txt`
  removed, renamed or reshaped (parameters, names, defaults, return type, settability), a member added that dependents must implement
  (`IBehaviour`, `IDirector`), or a documented behaviour that makes dependents' correct code wrong.
- **Patch** for everything else: fixes, new optional members, internal changes, a behaviour change that keeps dependents correct, and a port
  to a new Meitou with the same contract (with its own `gameVersion` floor).
- The contract is every public member of `IRivalryApi`, `IRivalryState`, `IRivalryRegistry`, `RivalryOptions`, `HostileCandidate`,
  `PartyClaim`, `PartyView`, `WaitingView`, `Rule`, `IBehaviour`, `IDirector`, `Assessment`, `Decisions`, `BuiltIn` and `RivalryMod`,
  and of every Rivalry Core type their signatures reach. `DependentContractTests` checks it against the list: a change at the same minor
  fails; an addition only asks for the list to be rewritten (`RIVALRY_WRITE_CONTRACT=1 dotnet test --filter DependentContract`). Behaviour
  is not listed: say in the PR when a documented behaviour changes.
- A minor bump's PR names each dependent, what it calls that changed and what it must do (a **Dependents** section), and widens their ranges.

# Production releases

Both `main` and `release` require pull requests. Promote production work to `release`; each protected PR requires two approving reviewers, including an applicable owner from CODEOWNERS. Authors cannot approve their own PRs. Do not push directly or bypass the review requirement.

Run production release workflows from the current `release` head. Release tags must point at that same commit. The central gate checks both the head and the actual enforced ruleset; a branch name alone is insufficient. Nightlies remain separate prereleases. Repositories without production packaging retain their build checks until a complete package can be produced.

Required ruleset setup and owner instructions: https://github.com/TheIvoryDisaster/GutenStructure/blob/main/docs/releases.md . Protection enforcement requires the scoped administration secret; configuration alone does not enable it.
