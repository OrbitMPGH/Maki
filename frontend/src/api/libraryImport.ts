import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

/** A folder dismissed from the library import of one root folder. */
export interface IgnoredImportFolder {
  id: number
  folderName: string
  createdAt: string
}

const ignoredKey = (rootFolderId: number | null) => ['libraryimport', 'ignored', rootFolderId] as const

export function useIgnoredImportFolders(rootFolderId: number | null) {
  return useQuery({
    queryKey: ignoredKey(rootFolderId),
    queryFn: () => api<IgnoredImportFolder[]>(`/libraryimport/ignored?rootFolderId=${rootFolderId}`),
    enabled: rootFolderId !== null,
  })
}

export function useIgnoreImportFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { rootFolderId: number; folderName: string }) =>
      api<void>('/libraryimport/ignored', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (_data, body) => queryClient.invalidateQueries({ queryKey: ignoredKey(body.rootFolderId) }),
  })
}

export function useUnignoreImportFolder(rootFolderId: number | null) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/libraryimport/ignored/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ignoredKey(rootFolderId) }),
  })
}
