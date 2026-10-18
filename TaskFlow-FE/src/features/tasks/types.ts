export interface TaskItem {
  id: string; projectId: string; title: string; description: string | null
  status: 'ToDo' | 'InProgress' | 'Done'; priority: 'Low' | 'Medium' | 'High'
  assigneeId: string | null; dueDateUtc: string | null; createdAtUtc: string; updatedAtUtc: string
}
export interface TaskPage { items: TaskItem[]; page: number; pageSize: number; totalCount: number; totalPages: number }
