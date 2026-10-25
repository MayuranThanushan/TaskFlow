import type { components } from '../../api/generated'

export type TaskItem = components['schemas']['TaskDto']
export type TaskPage = components['schemas']['TaskPage']
export type TaskCreate = components['schemas']['CreateTaskRequest']
export type TaskPatch = components['schemas']['TaskPatchRequest']
export type TaskStatusUpdate = components['schemas']['TaskStatusRequest']
export type TaskStatusResult = components['schemas']['TaskStatusDto']
