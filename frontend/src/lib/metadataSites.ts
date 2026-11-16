/** Display label and color for each known metadata site key. Colors are third-party brand colors, intentionally off-token. */
const SITES: Record<string, { label: string; short: string; color: string }> = {
  mangabaka: { label: 'MangaBaka', short: 'MB', color: 'orange' },
  anilist: { label: 'AniList', short: 'AL', color: 'blue' },
  myanimelist: { label: 'MyAnimeList', short: 'MAL', color: 'indigo' },
  mangaupdates: { label: 'MangaUpdates', short: 'MU', color: 'grape' },
  mangadex: { label: 'MangaDex', short: 'MD', color: 'orange.7' },
  kitsu: { label: 'Kitsu', short: 'KI', color: 'yellow' },
}

export function siteInfo(site: string) {
  return SITES[site] ?? { label: site, short: site.slice(0, 2).toUpperCase(), color: 'gray' }
}
