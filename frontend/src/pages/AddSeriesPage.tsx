import { useEffect } from 'react'
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { useLingui } from '@lingui/react/macro'
import { useAuth } from '../auth/AuthProvider'
import { CatalogueBrowser, type CatalogueSeed } from '../components/CatalogueBrowser'
import { PageHeader } from '../components/ui/PageHeader'

export default function AddSeriesPage() {
  const { t } = useLingui()
  // The command palette sends the text you typed there here as ?q= when the library holds no match.
  const [searchParams] = useSearchParams()
  const { can } = useAuth()

  // Discover's catalogue feeds hand over their Show more as router state. Dropped once used, or a
  // reload or a back/forward would put the feed back over whatever was browsed since.
  const location = useLocation()
  const navigate = useNavigate()
  const seed = (location.state as { catalogueSeed?: CatalogueSeed } | null)?.catalogueSeed ?? null
  useEffect(() => {
    if (seed) navigate(`${location.pathname}${location.search}`, { replace: true, state: null })
  }, [seed, navigate, location.pathname, location.search])

  // Same catalogue, same results, same detail modal: only the verb changes. Someone without
  // AddSeries files a request an admin actions instead of adding the series themselves.
  const canAdd = can('AddSeries')

  return (
    <>
      <PageHeader
        title={canAdd ? t`Add series` : t`Request series`}
        description={
          canAdd ? undefined : t`Browse or search MangaBaka and ask an admin for a title. You can ask for a chapter range too.`
        }
      />

      {/* No `idle` node, so an empty box browses the catalogue by popularity rather than showing an
          empty state. Filtering to a genre and a tag and never typing anything is a legitimate way
          to use this page, and the one the old search box could not do at all. Shares the Discover
          view and filter preferences, since it is the same catalogue seen from another door. */}
      <CatalogueBrowser
        scope="discover"
        seededQuery={searchParams.get('q')}
        seededBrowse={seed}
        placeholder={t`Search by title, description, or by feel`}
      />
    </>
  )
}
