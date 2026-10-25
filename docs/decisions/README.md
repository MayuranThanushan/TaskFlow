# Architecture decision register

Date: 18 October 2026. Status: adopted implementation baseline under the instruction to proceed with delivery step 01. These are project implementation decisions, not claims that a stakeholder separately signed off or that the features exist.

| Record | Decision | Status |
| --- | --- | --- |
| [0001](0001-structure-and-authentication.md) | Preserve workspace layout; layered API; cookie authentication and same-origin hosting | Adopted |
| [0002](0002-permissions-and-retention.md) | Owner/member access, one source of ownership, soft deletion and atomic history | Adopted |
| [0003](0003-data-and-api-semantics.md) | String IDs, UTC deadlines, PATCH presence, validation, calculations and queries | Adopted |
| [0004](0004-runtime-and-database-provider.md) | .NET/EF/Identity 10, Oracle MySQL provider, exact frontend pins and lockfiles | Adopted |
| [0005](0005-commercial-saas-boundaries.md) | Commercial company isolation, private-project roles, identity, v2 cutover and migration policy | Adopted design; implementation pending |

Related contracts: [API](../contracts/api-baseline.md), [database](../contracts/database-baseline.md). The Word BRD/SRS/SAD/API/ERD remain source requirements; the amendments explicitly resolve their open choices. Changing a decision requires updating this register, affected contracts, and relevant tests.

Runtime/provider package selection is resolved by decision 0004. Initial MySQL migration and persistence checks passed in step 05; shared test infrastructure remains step 06. See [database setup](../database-setup.md), see [setup-review.md](../setup-review.md) and [the toolchain guide](../toolchain.md).
