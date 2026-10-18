export interface Project {
  id: string
  name: string
  description: string | null
  ownerId: string
  createdAtUtc: string
  updatedAtUtc: string
}
export interface ProjectDetails extends Project {
  totalTasks: number
  completedTasks: number
  progressPercentage: number
}
export interface ProjectPage {
  items: Project[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}
