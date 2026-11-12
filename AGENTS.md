# Project guidance

- The ASP.NET Core web server and browser UI are the maintained application. The native Windows WPF and Linux Avalonia viewers have been removed; keep the application web-only. Windows and Linux remain supported server hosts.
- Follow the repository's `.editorconfig` and Microsoft C# conventions, including PascalCase for types and public members, camelCase for locals and parameters, and braces on separate lines. Apply SOLID principles and keep presentation, persistence, and camera playback responsibilities separate.
- Do not impose arbitrarily determined artificial limits on features or configuration. Necessary constraints must follow actual technical requirements or available resources; make resource budgets configurable where practical and explain the constraint.

# Required workflow for every requested change

- Use SOLID principles and standard Microsoft C# coding standards for all code changes.
- Implement comprehensive unit tests for new or changed behavior, covering normal cases, edge cases, and failure paths. Run the relevant tests and report the results. For documentation-only changes, validate the documentation rather than adding unrelated unit tests.
- Perform each requested change in its own dedicated Git worktree and branch, using the `codex/` prefix for the branch by default.
- When the change is complete and validated, commit it, push the branch, and create a pull request in the repository's hosting service. Include a summary of the change and validation results, and provide the PR link. If access or tooling prevents pushing or creating the PR, report the blocker explicitly.
- When the work is done, offer to remove its worktree and branch; retain them until the user accepts cleanup.
- These requirements apply automatically; the user does not need to repeat them with each change request.
