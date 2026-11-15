import { ApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { components, operations } from '../../api/generated'

export type TaskBoard = components['schemas']['TaskBoardDto']
export type BoardStatus = components['schemas']['TaskBoardStatusCountDto']['status']
export type BoardFilters = NonNullable<operations['WorkspaceReads_Board']['parameters']['query']>
export type BoardColumnQuery = NonNullable<operations['WorkspaceReads_BoardColumn']['parameters']['query']>
export type BoardColumn = components['schemas']['TaskPage']

function boardPath(client: ApiClient, projectId: string) {
  if (!client.resourceKey.startsWith('workspace:')) throw new ApiError('Select a company before opening a board.')
  if (!/^[1-9][0-9]*$/.test(projectId) || BigInt(projectId) > 9223372036854775807n) throw new ApiError('Invalid project.')
  return '/projects/' + projectId + '/board'
}

export function getBoard(client: ApiClient, projectId: string, filters: BoardFilters = {}, signal?: AbortSignal) {
  return client.request<TaskBoard>({ url: boardPath(client, projectId), params: filters, signal })
}

export function getBoardColumn(client: ApiClient, projectId: string, status: BoardStatus, query: BoardColumnQuery = {}, signal?: AbortSignal) {
  if (!['ToDo', 'InProgress', 'Done'].includes(status)) throw new ApiError('Invalid board column.')
  return client.request<BoardColumn>({ url: boardPath(client, projectId) + '/columns/' + status, params: query, signal })
}
