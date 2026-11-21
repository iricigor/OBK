# Repository collaboration workflow

Follow this workflow for code changes in this repository. Do not treat a request
to implement something as approval to skip brainstorming or user verification.

## 1. Brainstorm before code work

Discuss the problem, possible solutions, tradeoffs, and proposed scope with the
user before editing code. Ask clarifying questions where needed and wait for
the user to agree on the approach.

## 2. Track the work before implementation

Before creating a tracking issue or draft pull request, review the repository's
open issues and open pull requests for potential overlap with the planned work.
Compare their descriptions and proposed changes with the agreed scope, not
just their titles. Report any potential overlaps to the user with links and
wait for the user to decide how to proceed before creating either item. If the
check cannot be completed, explain why and wait for the user's decision rather
than assuming there is no overlap.

Create a GitHub issue describing the agreed scope and acceptance criteria.
Then open a draft pull request targeting the intended base branch and include
`Closes #<issue-number>` in its description.

If GitHub requires a commit before opening the draft PR, first push a minimal
planning or scaffolding commit reflecting the agreed approach. Do not start
the substantive implementation until the issue and draft PR exist.

## 3. Implement on the pull request branch

Work on the PR branch, keep changes within the agreed scope, and update directly
related documentation. Run the relevant checks, commit the changes, and push
them so the PR reflects the current work. Keep it in draft while work or user
verification is pending.

## 4. Offer local verification

Once the changes are ready, offer to build and run the app from the local PR
branch. Use the repository's documented commands; if no build is required,
explain that and offer to run the app directly.

Ask the user to verify the changed behavior, with concise steps and expected
results. Wait for their feedback. If changes are needed, implement and push
them, then request verification again. Automated checks do not replace the
user's verification.

## 5. Offer to merge after verification

After the user confirms the changes work, offer to merge the PR and resolve
the issue. Verification alone is not permission to merge: wait for explicit
merge approval.

Before merging, ensure the pushed PR is current and required checks and reviews
are satisfied; mark it ready for review if still in draft. After merging,
confirm that the linked issue closed. If it did not close automatically,
resolve it as part of the approved merge-and-resolve action.
