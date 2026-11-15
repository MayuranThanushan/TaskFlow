import { ApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { ProjectDetails, ProjectMemberList } from '../projects/types'
import type { TaskItem } from '../tasks/types'
import { getBoard, getBoardColumn } from './board-api'
import type { BoardColumn, BoardMove, BoardStatus, TaskBoard } from './board-api'

export const statuses: BoardStatus[] = ['ToDo', 'InProgress', 'Done']
export const statusLabel = { ToDo: 'To do', InProgress: 'In progress', Done: 'Completed' }
export const pageSize = 20
export type Pages = Record<BoardStatus, number>
export type BoardSnapshot = { project: ProjectDetails; members: ProjectMemberList; summary: TaskBoard; columns: Record<BoardStatus, BoardColumn> }
export type Placement = { move: BoardMove; index: number }

export async function readBoard(client: ApiClient, projectId: string, pages: Pages, signal: AbortSignal): Promise<BoardSnapshot> {
  const path = '/projects/' + encodeURIComponent(projectId)
  const [project, members, summary, ...columns] = await Promise.all([
    client.request<ProjectDetails>({ url: path, signal }),
    client.request<ProjectMemberList>({ url: path + '/members', signal }),
    getBoard(client, projectId, {}, signal),
    ...statuses.map(status => getBoardColumn(client, projectId, status, { page: pages[status], pageSize }, signal)),
  ])
  if (project.totalTasks !== summary.totalCount || statuses.some((status, i) => summary.columns.find(c => c.status === status)?.totalCount !== columns[i].totalCount)) {
    throw new ApiError('The board changed while loading. Reload to see current counts and cards.')
  }
  return { project, members, summary, columns: { ToDo: columns[0], InProgress: columns[1], Done: columns[2] } }
}

// Read-only preparation, before user confirmation. Never called to silently retry a rejected write.
export async function readPlacement(client: ApiClient, projectId: string, status: BoardStatus, sourceId: string, signal: AbortSignal) {
  const first = await getBoardColumn(client, projectId, status, { page: 1, pageSize: 100 }, signal)
  if (first.totalCount > 500 || first.totalPages > 5) throw new ApiError('This column exceeds the supported ordering limit. Contact the company owner.')
  const rest = await Promise.all(Array.from({ length: Math.max(0, first.totalPages - 1) }, (_, i) => getBoardColumn(client, projectId, status, { page: i + 2, pageSize: 100 }, signal)))
  const pages = [first, ...rest]
  const items = pages.flatMap(p => p.items)
  if (pages.some((p, i) => p.page !== i + 1 || p.totalCount !== first.totalCount || p.totalPages !== first.totalPages)
      || items.length !== first.totalCount || new Set(items.map(t => t.id)).size !== items.length || items.some(t => t.status !== status)) {
    throw new ApiError('The column changed while choosing a position. Reload positions before confirming.')
  }
  return { items: items.filter(t => t.id !== sourceId), sourceIndex: items.findIndex(t => t.id === sourceId) }
}

export function placement(status: BoardStatus, items: TaskItem[], index: number): Placement {
  const previous = items[index - 1], next = items[index]
  return { index, move: { status, previousTaskId: previous?.id ?? null, previousVersion: previous?.version ?? null, nextTaskId: next?.id ?? null, nextVersion: next?.version ?? null } }
}

export function optimisticMove(snapshot: BoardSnapshot, task: TaskItem, target: Placement): BoardSnapshot {
  const columns = { ...snapshot.columns }
  for (const status of statuses) {
    if (status !== task.status && status !== target.move.status) continue
    const column = columns[status]
    const count = column.totalCount + (task.status === target.move.status ? 0 : status === task.status ? -1 : 1)
    const items = column.items.filter(t => t.id !== task.id)
    const offset = (column.page - 1) * column.pageSize
    if (status === target.move.status && target.index >= offset && target.index < offset + column.pageSize) items.splice(target.index - offset, 0, { ...task, status })
    columns[status] = { ...column, items: items.slice(0, column.pageSize), totalCount: count, totalPages: Math.ceil(count / column.pageSize) }
  }
  const completed = snapshot.project.completedTasks + (task.status === 'Done' ? -1 : 0) + (target.move.status === 'Done' ? 1 : 0)
  return { ...snapshot, columns, summary: { ...snapshot.summary, columns: snapshot.summary.columns.map(c => ({ ...c, totalCount: columns[c.status].totalCount })) }, project: { ...snapshot.project, completedTasks: completed, progressPercentage: snapshot.project.totalTasks ? Math.round(completed * 100 / snapshot.project.totalTasks) : 0 } }
}
