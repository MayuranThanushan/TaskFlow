# Company and private-project permission contract

Status: adopted by [ADR 0005](../decisions/0005-commercial-saas-boundaries.md); future implementation, not current authorization behavior.

All permissions require an authenticated global account and active membership in the addressed workspace. Suspension takes precedence over any project role. Missing membership means no company access. Roles in different companies never combine. No endpoint accepts a client-supplied effective role.

## Company capabilities

| Capability | Owner | Admin | Member | Guest |
| --- | --- | --- | --- | --- |
| View basic company identity and own membership | Yes | Yes | Yes | Yes |
| List company directory, excluding private-project membership | Yes | Yes | Yes | No |
| Create private project | Yes | Yes | Yes | No |
| Edit ordinary company settings | Yes | Yes | No | No |
| Invite/manage Member or Guest; suspend those roles | Yes | Yes | No | No |
| Appoint/remove/suspend Admin | Yes | No | No | No |
| Appoint/remove/suspend Owner | Yes, owner invariants | No | No | No |
| Read redacted company governance audit | Yes | Yes | No | No |
| Billing, company closure, ownership transfer | Yes, sensitive checks | No | No | No |
| Read every private project or add self to it | No | No | No | No |

There is no delegated billing capability in the initial release. Owner grant/revoke and company closure require recent reauthentication; enforce configured MFA policy when available. Never suspend, remove or demote the last active Owner. A principal cannot promote itself through a membership update. Member directory fields are limited to ID/display name; expose email only to authorized team administrators where needed. Guests see collaborators only inside accessible projects.

## Project capabilities

These roles apply only within the same workspace. Workspace Owner/Admin alone does not satisfy any cell below. A Guest may be Editor/Viewer but cannot own or create a project. Owner transfer requires an active non-Guest workspace member.

| Capability | Project Owner | Editor | Viewer | No project access |
| --- | --- | --- | --- | --- |
| Read project/tasks/members/activity | Yes | Yes | Yes | No |
| Create/edit/assign tasks; change status; future comments/board moves | Yes | Yes | No | No |
| Delete task | Yes | No | No | No |
| Rename/edit/delete project | Yes | No | No | No |
| Grant/revoke Editor/Viewer to eligible company members | Yes | No | No | No |
| Transfer project ownership | Yes, validated recipient | No | No | No |

Company invitations are a company-admin action; a project owner can add only existing active company members initially. Future project-scoped invitations must compose both capabilities or request admin approval. Assignments require active same-company membership plus project access; Viewer assignment is rejected because the recipient cannot act on the task. Existing legacy collaborators become Editors, preserving assignment eligibility.

## Departure, suspension and recovery

Normal project-owner departure requires transfer first. Company departure/removal must transfer owned projects and protect the last active company owner. Suspension blocks new requests immediately after commit even if project handover is incomplete; already-authorized transactions may finish under the existing serialization policy. Clear active assignments atomically, retain historical attribution and memberships for audit, and invalidate relevant access caches.

An orphaned project is not automatically readable by Admin. Pilot recovery is an operator-reviewed, company-owner-authorized custody transfer using a separate capability, recorded reason, target, expiry and immutable audit. Do not ship a recovery endpoint until this workflow and its negative tests exist. A suspended sole project owner must not force reinstatement just to restore custody. Customer content remains inaccessible to support unless a separate explicit, time-limited break-glass grant authorizes it; ordinary support gets diagnostics only.

Company audit readers can see governance action, stable entity ID, actor and outcome; titles, task bodies, comments, snapshots and deltas from private projects are redacted without current project read permission. Deleted-project governance review is a distinct restricted query, never a bypass on ordinary project routes. Global sign-in/security records are platform-restricted, not copied into every company a user belongs to.
