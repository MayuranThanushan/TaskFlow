import type { components, operations, paths } from './generated'
import type { Profile } from './types'
import type { TaskItem, TaskPatch, TaskStatusUpdate } from '../features/tasks/types'

type Assert<T extends true> = T
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false

// Compile-time consumers of the external contract. These fail if numeric IDs,
// nullable required fields, status allowlists or PATCH nullability drift.
export type ContractChecks = [
  Assert<Equal<Profile['id'], string>>,
  Assert<Equal<TaskItem['assigneeId'], string | null>>,
  Assert<Equal<TaskItem['description'], string | null>>,
  Assert<Equal<TaskPatch['title'], string | undefined>>,
  Assert<Equal<TaskPatch['description'], string | null | undefined>>,
  Assert<Equal<TaskPatch['priority'], 'Low' | 'Medium' | 'High' | undefined>>,
  Assert<Equal<TaskStatusUpdate['status'], 'ToDo' | 'InProgress' | 'Done'>>,
  Assert<Equal<components['schemas']['DashboardDto']['totalTasks'], number>>,
  Assert<Equal<components['schemas']['ApiProblem']['traceId'], string>>,
  Assert<Equal<keyof components['schemas']['ProjectWriteRequest'], 'name' | 'description'>>,
  Assert<Equal<components['schemas']['WorkspaceDto']['id'], string>>,
  Assert<Equal<components['schemas']['WorkspaceDto']['role'], 'Owner' | 'Admin' | 'Member' | 'Guest'>>,
  Assert<Equal<operations['WorkspaceReads_Projects']['parameters']['path']['workspaceId'], string>>,
  Assert<Equal<paths['/api/v2/workspaces/{workspaceId}/projects']['post'], operations['WorkspaceWrites_CreateProject']>>,
]
