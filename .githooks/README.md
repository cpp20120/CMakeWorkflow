# Local pre-commit formatting check

Enable the versioned hook **once per clone**:

```sh
git config --local core.hooksPath .githooks
```

If a hooks path was already configured (`git config --local --get core.hooksPath`),
review that configuration before changing it, because Git supports only one
active hooks directory.

The hook checks **only staged `.cs` files** using `dotnet format whitespace`
and `dotnet format style`, both with `--verify-no-changes` and `--include`.
It never formats, stages, or overwrites files; `.editorconfig` settings apply.

Before running `git commit`, build/restore the solution at least once because
the hook uses `--no-restore`. If a staged file also has unstaged changes, the
commit is refused: `dotnet format` sees the worktree, not the staged Git blob.

For a formatting failure, format the affected file(s) explicitly, then re-stage:

```sh
dotnet format whitespace CMakeWorkflow.slnx --include path/to/file.cs
dotnet format style CMakeWorkflow.slnx --severity warn --include path/to/file.cs
git add -- path/to/file.cs
```

Do **not** run either formatting command over the whole solution just to fix
one staged file. `git add -A` can stage hundreds of unrelated changed files;
review `git diff --cached --stat` before committing.

The hook is a convenience check, not a security boundary or a replacement for
CI's `dotnet build ... -p:TreatWarningsAsErrors=true`.
