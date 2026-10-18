# 0003: Data representation and API semantics

Status: adopted. Date: 18 October 2026.

## Context

The draft API uses numeric example IDs despite BIGINT database keys. PATCH omission/null semantics, exact text limits and deadline interpretation are unspecified. The API examples lack the SRS-required project progress fields.

## Decision

Persist signed BIGINT keys (C# long) including Identity keys. Serialize all identifiers as positive base-10 strings in JSON, paths and TypeScript DTOs. Validate range 1..9223372036854775807 without a JavaScript number conversion. A string ID is opaque to the frontend. This explicitly supersedes numeric ID examples in the Word API specification.

Store UTC timestamps in DATETIME(6); send ISO 8601 with Z. Deadline inputs must include Z or an explicit offset and are normalized to UTC. Due dates represent date/time instants, not date-only deadlines. Past deadlines are valid; users see local time. Use an injectable clock for boundary tests.

Required trimmed text: displayName 1–100, project name 1–150, task title 1–200 characters. Optional trimmed descriptions: maximum 10,000, null allowed, whitespace-only normalized to null. Email is trimmed, maximum 256, normalized through Identity and uniquely constrained on NormalizedEmail. Password follows decision 0001 and is never trimmed. Frontend and .NET validation use UTF-16 code-unit lengths consistently.

Status strings are ToDo/InProgress/Done, shown as To Do/In Progress/Completed. Priority strings are Low/Medium/High. Store enum names explicitly. New tasks are ToDo; any transition between valid statuses is allowed. Creating with a status field is rejected. Default omitted creation priority to Medium. Undefined enum strings/numeric enum values fail validation.

For PATCH, omitted properties are unchanged; explicit null clears description, assigneeId or dueDateUtc. Null/blank required fields fail. Reject unknown properties, server-managed fields, empty PATCH bodies and duplicate JSON keys. Task status is changed only through `/tasks/{taskId}/status`. Use presence-aware request parsing rather than treating all nullable DTO properties as absent.

Progress = round(completed active tasks / total active tasks *100), with midpoint rounding away from zero; zero tasks gives 0. Overdue means dueDateUtc < nowUtc and status != Done. Dashboard totals include each accessible active project/task once; assignedToMe uses this same scope. Capture one current timestamp per aggregation request. Use one aggregate statement or a consistent transaction snapshot for mutually dependent counts.

Pagination: defaults page=1/pageSize=20, maximum pageSize=100. Reject invalid numbers, excessive sizes, unsupported sort/filter values and reversed activity time ranges with 400. Guard offset arithmetic against overflow. A page beyond results returns empty items with actual totals; zero results yields totalPages=0. Default createdAtUtc desc then id desc. Sort allowlists and null behavior are in the API amendment. Title search is a literal case-insensitive substring using the selected MySQL utf8mb4 collation; escape LIKE wildcard characters if constructing LIKE patterns.

## Consequences and verification

Frontend types use string IDs everywhere. Presence-aware PATCH requires dedicated parsing tests. Server owns validation and all aggregates. Verify ID boundaries, omitted/null/empty/unknown fields, enum behavior, UTF-16 length boundaries, timezone offsets, deadline equality, zero/rounding cases, stable sorting and authorization before pagination/counting. See the API and database amendments for exact implementation contracts.
