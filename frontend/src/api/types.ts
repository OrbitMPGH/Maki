export interface SeriesDto {
  id: number
  title: string
  sortTitle: string
  originalTitle: string | null
  status: string
  overview: string | null
  year: number | null
  genres: string[]
  monitored: boolean
  monitorNewItems: string
  rootFolderId: number
  folderName: string
  coverUrl: string | null
  totalChapters: number | null
  totalVolumes: number | null
  authorStory: string | null
  authorArt: string | null
  mangaBakaId: number | null
  aniListId: number | null
  malId: number | null
  added: string
  chapterCount: number
  chapterFileCount: number
}

export interface MetadataSearchResult {
  providerId: string
  title: string
  coverUrl: string | null
  year: number | null
  status: string
  description: string | null
  totalChapters: number | null
}

export interface RootFolder {
  id: number
  path: string
  freeSpace: number | null
  accessible: boolean
}

export interface AddSeriesRequest {
  metadataProviderId: string
  rootFolderId: number
  monitored: boolean
  monitorNewItems: string
}
