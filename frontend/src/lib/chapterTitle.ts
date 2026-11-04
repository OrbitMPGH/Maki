import type { ChapterDto } from '../api/types'

const WHOLE_TAG = /^[[(]([^\])]+)[\])]$/
const TAGS = /[[(]([^\])]+)[\])]/g

/**
 * The chapter's title as a row shows it. A volume pack parsed without chapter names leaves the
 * release group as the title ("(1r0n)"), which reads like a title on every row. Only a title that is
 * a single bracketed tag, and that same tag in the chapter's own release or file name, counts as the
 * group; anything else, including a real title in brackets, passes through.
 */
export function displayChapterTitle(chapter: Pick<ChapterDto, 'title' | 'fileReleaseName' | 'filePath'>): string | null {
  const title = chapter.title?.trim()
  if (!title) return null
  const tag = WHOLE_TAG.exec(title)?.[1].trim().toLocaleLowerCase()
  if (!tag) return title
  const fileName = chapter.filePath?.split(/[\\/]/).pop()
  for (const name of [chapter.fileReleaseName, fileName]) {
    if (!name) continue
    for (const m of name.matchAll(TAGS)) {
      if (m[1].trim().toLocaleLowerCase() === tag) return null
    }
  }
  return title
}
