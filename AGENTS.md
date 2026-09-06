# Repository safety rules

These rules apply to every Codex or ChatGPT-assisted change in this Unity project.

## Before editing

1. Never work directly on main.
2. Run Tools/RepoSafety/Start-CodexTask.ps1 -Task <short-name> from the stable Unity checkout.
3. If either the Unity or Python checkout is dirty, stop and report the exact files. Do not stash, reset, overwrite, or commit user changes automatically.
4. Work only in the paired worktrees printed by the start script.

## While editing

1. Keep Unity and Python changes under the same task branch name.
2. After each coherent stage passes validation, run Save-CodexCheckpoint.ps1 and push the paired checkpoint.
3. A paused, unverified stage may use Save-CodexCheckpoint.ps1 -Wip; WIP commits are not stable rollback points and receive no checkpoint tag.
4. Never run git reset --hard, force-push, delete a checkpoint tag, or push directly to main.
5. Do not add credentials, generated Unity folders, Python environments, logs, recordings, or ordinary Git objects larger than 100 MiB.

## Finishing and recovery

1. Open linked pull requests for the Unity and Python branches. The user must approve both before either is merged.
2. Stable checkpoints use the same permanent checkpoint/... tag in both repositories, even when only one repository changed.
3. Recover with Tools/RepoSafety/Restore-CodexCheckpoint.ps1 -Checkpoint <tag>. Recovery creates new worktrees and preserves the failed branch.
4. If one push fails, keep the successful remote tag and retry the same checkpoint ID; never replace it with a different commit.
