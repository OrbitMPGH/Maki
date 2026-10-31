import { http, HttpResponse } from 'msw'
import type { FolderListing } from '../../../src/api/hooks'
import { api } from '../api'

/** A small Linux-shaped tree for the root folder picker. */
const tree: Record<string, string[]> = {
  '/': ['config', 'data', 'library'],
  '/data': ['downloads', 'media'],
  '/data/media': ['comics', 'manga'],
  '/data/media/manga': [],
  '/data/media/comics': [],
  '/data/downloads': [],
  '/library': ['manga', 'new-shelf'],
  '/library/manga': [],
  '/library/new-shelf': [],
  '/config': [],
}

const join = (parent: string, name: string) => (parent === '/' ? `/${name}` : `${parent}/${name}`)

export const browseFoldersHandler = http.get(api('/rootfolder/browse'), ({ request }) => {
  const path = new URL(request.url).searchParams.get('path') || '/'
  const children = tree[path]
  if (!children) {
    return HttpResponse.json(
      { code: 'error.rootFolder.doesNotExist', error: `Folder does not exist: ${path}` },
      { status: 400 },
    )
  }
  const parent = path === '/' ? null : path.slice(0, path.lastIndexOf('/')) || '/'
  return HttpResponse.json({
    path,
    parent,
    folders: children.map((name) => ({ name, path: join(path, name) })),
  } satisfies FolderListing)
})
