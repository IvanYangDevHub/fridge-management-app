# Issue tracker: GitHub

Issues and specs for this repository live in `IvanYangDevHub/fridge-management-app` GitHub Issues. Use `gh` from this clone.

## Conventions

- Before creating an issue, confirm `gh repo view --json nameWithOwner` targets `IvanYangDevHub/fridge-management-app`.
- Create issues with `gh issue create --title "..." --body-file <file>`.
- Read issues with `gh issue view <number> --json number,title,body,labels,comments`.
- List and filter issues with `gh issue list`; use issue labels from `docs/agents/triage-labels.md`.
- Add or remove labels with `gh issue edit <number> --add-label/--remove-label`.
- Comment with `gh issue comment <number> --body-file <file>`.
- Use native GitHub sub-issues and blocking dependencies when available; otherwise record parent and blocker issue numbers in the body.
- Do not treat pull requests as triage requests.

## Skill operations

- "Publish to the issue tracker" means create a GitHub issue.
- "Fetch the relevant ticket" means read its GitHub issue and comments.
